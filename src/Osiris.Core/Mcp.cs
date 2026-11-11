using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Osiris.Core;

/// <summary>
/// The workspace's MCP servers (<c>"mcp"</c> in <c>.osiris/osiris.json</c>: <c>{"name": {"url", "headers", "enabled"}}</c>,
/// plus the ones its packages bring), their tools offered to the agent as <c>&lt;server&gt;_&lt;tool&gt;</c>. Connections open on first use and are kept
/// until the file changes or <see cref="CacheTtl"/> passes; a server that fails is left out and named in the prompt.
/// The URL (after the host) and headers may hold <c>{{secret:NAME}}</c>, filled for the secret's allowed hosts.
/// </summary>
public sealed partial class Mcp(Workspace workspace, bool allowPrivate, Secrets secrets) : IAsyncDisposable
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Replaced connections stay open this long, so runs already using their tools can finish.</summary>
    static readonly TimeSpan CloseGrace = TimeSpan.FromMinutes(2);

    readonly string file = workspace.ConfigFile;
    readonly SemaphoreSlim gate = new(1, 1);
    List<McpClient> clients = [];
    IList<AITool>? tools;
    DateTime stamp;
    DateTime loadedAt;

    /// <summary>The configured servers, by name.</summary>
    public IReadOnlyDictionary<string, McpServerConfig> Servers()
    {
        try { return ReadConfig(); }
        catch (ConfigurationException) { return new Dictionary<string, McpServerConfig>(); }
    }

    /// <summary>Adds or changes a server; null <paramref name="headers"/> keep the stored ones. Its tools reload on the next run.</summary>
    public void Put(string name, string url, Dictionary<string, string>? headers, bool enabled)
    {
        if (!ServerName().IsMatch(name)) throw new ArgumentException("The name must be lowercase-kebab-case, e.g. my-crm.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http")) throw new ArgumentException($"Invalid URL: {url}");
        var servers = new Dictionary<string, McpServerConfig>(ReadConfig());
        servers[name] = new McpServerConfig(url, headers ?? servers.GetValueOrDefault(name)?.Headers, enabled);
        WriteConfig(servers);
    }

    public bool Delete(string name)
    {
        var servers = new Dictionary<string, McpServerConfig>(ReadConfig());
        if (!servers.Remove(name)) return false;
        WriteConfig(servers);
        return true;
    }

    IReadOnlyDictionary<string, McpServerConfig> ReadConfig() => OsirisConfig.Load(file).Mcp;

    void WriteConfig(Dictionary<string, McpServerConfig> servers) => OsirisConfig.Update(file, "mcp", servers.Count > 0 ? servers : null);

    /// <summary>Servers that couldn't be used at the last load, with why.</summary>
    public IReadOnlyDictionary<string, string> Unavailable { get; private set; } = new Dictionary<string, string>();

    /// <summary>The tools of the configured servers and of <paramref name="fromPackages"/> (a configured server of the same name wins).</summary>
    public async Task<IList<AITool>> ToolsAsync(IReadOnlyList<McpServerDefinition>? fromPackages = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
            if (tools is not null && now == stamp && DateTime.UtcNow - loadedAt < CacheTtl) return tools;
            var old = clients;
            _ = Task.Delay(CloseGrace).ContinueWith(_ => CloseAsync(old));
            (clients, tools) = ([], []);
            await LoadAsync(fromPackages ?? [], ct);
            (stamp, loadedAt) = (now, DateTime.UtcNow);
            return tools;
        }
        finally
        {
            gate.Release();
        }
    }

    async Task LoadAsync(IReadOnlyList<McpServerDefinition> fromPackages, CancellationToken ct)
    {
        var unavailable = new Dictionary<string, string>();
        var servers = fromPackages.DistinctBy(s => s.Name).ToDictionary(s => s.Name, s => new McpServerConfig(s.Url, s.Headers?.ToDictionary()));
        try
        {
            foreach (var (name, server) in ReadConfig()) servers[name] = server;
        }
        catch (ConfigurationException e)
        {
            Unavailable = new Dictionary<string, string> { ["osiris.json"] = e.Message };
            return;
        }

        // One client per server, so a failing server is isolated.
        var opened = await Task.WhenAll(servers.Where(s => s.Value.Enabled).Select(async s =>
        {
            try { return (s.Key, Client: await ConnectAsync(s.Key, s.Value, ct), Error: ""); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return (s.Key, Client: (McpClient?)null, Error: e.Message); }
        }));
        foreach (var (name, client, error) in opened)
        {
            if (client is null) { unavailable[name] = error; continue; }
            try
            {
                clients.Add(client);
                foreach (var tool in await client.ListToolsAsync(cancellationToken: ct)) tools!.Add(tool.WithName($"{name}_{tool.Name}"));
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { unavailable[name] = e.Message; }
        }
        Unavailable = unavailable;
    }

    async Task<McpClient> ConnectAsync(string name, McpServerConfig server, CancellationToken ct)
    {
        if (!ServerName().IsMatch(name)) throw new InvalidOperationException("The name must be lowercase-kebab-case.");
        var headers = server.Headers ?? [];
        var texts = headers.ToDictionary(h => $"h:{h.Key}", h => h.Value);
        texts["url"] = server.Url;
        var (filled, used) = secrets.Fill(texts, Secrets.HostOf(server.Url));
        var url = await SafeUrl.CheckAsync(filled["url"], allowPrivate, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = url,
            Name = name,
            AdditionalHeaders = headers.Keys.ToDictionary(k => k, k => filled[$"h:{k}"]),
        });
        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No answer within {ConnectTimeout.TotalSeconds} s.");
        }
        catch (Exception e) when (used.Count > 0 && e is not OperationCanceledException)
        {
            // A message may quote the filled URL: the secrets in it never leave.
            throw new InvalidOperationException(Secrets.Redact(e.Message, used));
        }
    }

    /// <summary>The prompt's note on servers that failed, so the model can say why their tools are missing.</summary>
    public string Prompt() => Unavailable.Count == 0 ? "" :
        $"MCP servers unavailable now (their tools are missing): {string.Join("; ", Unavailable.Select(u => $"{u.Key}: {u.Value}"))}";

    static async Task CloseAsync(List<McpClient> clients)
    {
        foreach (var c in clients)
        {
            try { await c.DisposeAsync(); }
            catch (Exception) { /* a server gone already */ }
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync(clients);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ServerName();
}
