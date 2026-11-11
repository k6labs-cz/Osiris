using System.Text.Json;
using System.Text.Json.Nodes;

namespace Osiris.Core;

/// <summary>
/// The model the agent talks to: any OpenAI-compatible API. A missing value falls back to the user's or the built-in default.
/// The key is never in plain text: <c>{{secret:NAME}}</c> (a workspace secret allowed for the endpoint's host) or <c>{{env:NAME}}</c>.
/// </summary>
public sealed record ModelConfig(string? Endpoint = null, string? Name = null, string? Embedding = null, string? ApiKey = null)
{
    public const string DefaultEndpoint = "https://api.mistral.ai/v1";

    public static ModelConfig Default { get; } = new(DefaultEndpoint, "mistral-small-latest", "mistral-embed");

    /// <summary>This configuration with <paramref name="fallback"/>'s values where it has none.</summary>
    public ModelConfig Over(ModelConfig fallback) => new(Endpoint ?? fallback.Endpoint, Name ?? fallback.Name, Embedding ?? fallback.Embedding, ApiKey ?? fallback.ApiKey);
}

/// <summary>A NuGet package with tools, guards or MCP servers for the agent.</summary>
public sealed record PackageReference(string Id, string Version, JsonElement Settings);

public sealed record McpServerConfig(string Url, Dictionary<string, string>? Headers = null, bool Enabled = true);

/// <summary>
/// <c>osiris.json</c>: the whole configuration of an agent. In a workspace's <c>.osiris/</c> it configures that
/// workspace; the same file in the user's configuration folder gives the defaults for all of them.
/// <code>
/// {
///   "model":    { "endpoint": "https://api.openai.com/v1", "name": "gpt-5", "embedding": "text-embedding-3-small", "apiKey": "{{env:OPENAI_API_KEY}}" },
///   "packages": { "k6labs.Osiris.Tools.Database": "1.0.0",
///                 "Acme.Osiris.Crm": { "version": "2.1.0", "settings": { "region": "eu" } } },
///   "sources":  [ "https://nuget.pkg.github.com/acme/index.json" ],
///   "mcp":      { "crm": { "url": "https://crm.example/mcp", "headers": { "Authorization": "Bearer {{secret:CRM_TOKEN}}" } } }
/// }
/// </code>
/// Everything is optional. A file that can't be read throws <see cref="ConfigurationException"/>.
/// </summary>
public sealed record OsirisConfig(ModelConfig Model, IReadOnlyList<PackageReference> Packages, IReadOnlyList<string> Sources, IReadOnlyDictionary<string, McpServerConfig> Mcp)
{
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly JsonElement NoSettings = JsonDocument.Parse("{}").RootElement;

    public static OsirisConfig Empty { get; } = new(new ModelConfig(), [], [], new Dictionary<string, McpServerConfig>());

    public static OsirisConfig Load(string file)
    {
        if (!File.Exists(file)) return Empty;
        try
        {
            if (ReadRoot(file) is not { } root) return Empty;
            return new OsirisConfig(
                root["model"].Deserialize<ModelConfig>(Web) ?? new ModelConfig(),
                [.. (root["packages"]?.AsObject() ?? []).Select(p => Package(p.Key, p.Value))],
                root["sources"].Deserialize<string[]>(Web) ?? [],
                root["mcp"].Deserialize<Dictionary<string, McpServerConfig>>(Web) ?? []);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new ConfigurationException($"{file} can't be read: {e.Message}");
        }
    }

    /// <summary>Rewrites one section of the file (<c>null</c> removes it), leaving the others as they are.</summary>
    public static void Update(string file, string section, object? value)
    {
        var root = (File.Exists(file) ? ReadRoot(file) : null) ?? [];
        if (value is null) root.Remove(section);
        else root[section] = JsonSerializer.SerializeToNode(value, Web);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Web) + "\n");
        File.Move(temp, file, overwrite: true);
    }

    static JsonObject? ReadRoot(string file) => JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient)?.AsObject();

    /// <summary><c>"Id": "1.0.0"</c> or <c>"Id": { "version": "1.0.0", "settings": { … } }</c>.</summary>
    static PackageReference Package(string id, JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var version) => new(id, version, NoSettings),
        JsonObject o when o["version"]?.GetValue<string>() is { } version => new(id, version, o["settings"] is { } s ? JsonSerializer.SerializeToElement(s) : NoSettings),
        _ => throw new FormatException($"package {id} needs a version (\"{id}\": \"1.0.0\")"),
    };
}

/// <summary>A configuration that can't be used; the message says what to fix.</summary>
public sealed class ConfigurationException(string message) : Exception(message);
