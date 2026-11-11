using System.Text.Json;

namespace Osiris;

/// <summary>
/// What an extension gets from the agent that loads it. A tool provider, guard or MCP server provider takes it
/// in its constructor (or has a constructor without parameters).
/// </summary>
/// <param name="Workspace">The workspace the agent works over.</param>
/// <param name="Settings">
/// The package's <c>settings</c> from <c>.osiris/osiris.json</c>
/// (<c>"packages": { "My.Package": { "version": "1.0.0", "settings": { … } } }</c>); an empty object when there are none.
/// </param>
/// <param name="AllowPrivateUrls">
/// True for an agent on the user's own machine (CLI, desktop). False on a shared server, where an extension must not
/// reach localhost or private networks on the workspace's behalf.
/// </param>
public sealed record OsirisContext(IWorkspace Workspace, JsonElement Settings, bool AllowPrivateUrls)
{
    /// <summary>The settings as a type of the extension's own; <c>null</c> properties when a value is missing.</summary>
    public T? GetSettings<T>() => Settings.ValueKind == JsonValueKind.Object ? Settings.Deserialize<T>(JsonSerializerOptions.Web) : default;
}
