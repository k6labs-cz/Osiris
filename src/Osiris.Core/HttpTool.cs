using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.AI;

namespace Osiris.Core;

/// <summary>
/// <c>http_request</c>: one request to a web API (typically as a skill says), with <c>{{secret:NAME}}</c> in the URL,
/// headers or body filled for the secret's allowed hosts only. Public https addresses only (unless the agent runs on
/// the user's machine), redirects never followed (so a token isn't bounced elsewhere), the secrets masked in the answer.
/// </summary>
public sealed class HttpTool(Secrets secrets, bool allowPrivate) : IToolProvider
{
    IEnumerable<AITool> IToolProvider.GetTools() => Tools();

    const int MaxResponseChars = 20_000;
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public IList<AITool> Tools() => [AIFunctionFactory.Create(RequestAsync, "http_request")];

    [Description("Calls a web API. Put access keys as {{secret:NAME}} (listed under SECRETS), never write them out. Redirects aren't followed. " +
        "Make requests that change something only when the user asks for it.")]
    public async Task<string> RequestAsync(
        [Description("The full https URL")] string url,
        [Description("GET, POST, PUT, PATCH or DELETE")] string method = "GET",
        [Description("E.g. {\"Authorization\": \"Bearer {{secret:CRM_TOKEN}}\"}")] Dictionary<string, string>? headers = null,
        [Description("The request body, typically JSON")] string? body = null,
        CancellationToken ct = default)
    {
        method = method.ToUpperInvariant();
        if (!Methods.Contains(method)) return $"Refused: unknown method {method}.";
        headers ??= [];
        List<string> used = [];
        try
        {
            var texts = new Dictionary<string, string> { ["url"] = url, ["body"] = body ?? "" };
            foreach (var (k, v) in headers) texts[$"h:{k}"] = v;
            (var filled, used) = secrets.Fill(texts, Secrets.HostOf(url));
            var target = await SafeUrl.CheckAsync(filled["url"], allowPrivate, ct);

            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            if (body is not null && method is not "GET") request.Content = new StringContent(filled["body"]);
            foreach (var name in headers.Keys)
            {
                var value = filled[$"h:{name}"];
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { if (request.Content is not null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value); }
                else request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var head = string.Join('\n', new[] { $"HTTP {(int)response.StatusCode}", response.Content.Headers.ContentType?.ToString(), response.Headers.Location is { } l ? $"Location: {l}" : null }.OfType<string>());
            var shown = text.Length > MaxResponseChars ? text[..MaxResponseChars] + "\n… (truncated)" : text;
            return Secrets.Redact($"{head}\n\n{shown}", used);
        }
        catch (Exception e) when (e is SecretException or UnsafeUrlException)
        {
            return $"Refused: {e.Message}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // A message may quote the filled URL: the secrets in it never leave.
            return Secrets.Redact($"The request failed: {e.Message}", used);
        }
    }
}
