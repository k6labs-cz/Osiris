namespace Osiris;

/// <summary>An MCP server reached over HTTP (streamable HTTP or SSE).</summary>
/// <param name="Name">Lowercase kebab-case; the server's tools are offered as <c>&lt;name&gt;_&lt;tool&gt;</c>.</param>
/// <param name="Url">The endpoint. After the host it may hold <c>{{secret:NAME}}</c>.</param>
/// <param name="Headers">Request headers; values may hold <c>{{secret:NAME}}</c> (e.g. <c>Bearer {{secret:CRM_TOKEN}}</c>).</param>
public sealed record McpServerDefinition(string Name, string Url, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// MCP servers a package brings, next to the ones in <c>.osiris/osiris.json</c> (a server configured there under
/// the same name wins). Asked whenever the workspace's configuration changes.
/// </summary>
public interface IMcpServerProvider
{
    IEnumerable<McpServerDefinition> GetServers();
}
