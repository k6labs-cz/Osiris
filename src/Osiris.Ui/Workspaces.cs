using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Osiris.Core;

namespace Osiris.Ui;

/// <summary>Who may do what. The agent authorizes nothing; the host that knows its users does.</summary>
public interface IWorkspaceAccess
{
    /// <summary>Whether the user may talk to the workspace's agent.</summary>
    bool CanUse(ClaimsPrincipal user, string workspace);

    /// <summary>Whether the user may change the workspace's settings (secrets, MCP servers, rules, configuration).</summary>
    bool CanAdminister(ClaimsPrincipal user, string workspace);
}

/// <summary>A single-user app: the workspace is the user's own.</summary>
public sealed class OwnerAccess : IWorkspaceAccess
{
    public bool CanUse(ClaimsPrincipal user, string workspace) => true;
    public bool CanAdminister(ClaimsPrincipal user, string workspace) => true;
}

/// <summary>
/// The workspaces this process serves: one folder (the desktop app), or every folder under a root (the server),
/// each with one agent. A workspace's id is its folder's name.
/// </summary>
public sealed partial class Workspaces(AgentOptions host, string root, bool many, IWorkspaceAccess access) : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, Agent> agents = new();

    /// <summary>True on a server with many workspaces and users.</summary>
    public bool Many => many;

    IEnumerable<string> Ids() => many
        ? new DirectoryInfo(root).EnumerateDirectories().Select(d => d.Name).Where(s => Id().IsMatch(s)).Order()
        : [Path.GetFileName(root)];

    public IEnumerable<string> List(ClaimsPrincipal user) => Ids().Where(id => access.CanUse(user, id));

    /// <summary>The workspace's agent, or null when it doesn't exist or the user may not use it.</summary>
    public Agent? Get(string id, ClaimsPrincipal user) => List(user).Contains(id) ? AgentOf(id) : null;

    /// <summary>The workspace's agent for changing its settings, or null when the user may not.</summary>
    public Agent? Administer(string id, ClaimsPrincipal user) => access.CanAdminister(user, id) ? Get(id, user) : null;

    public bool CanAdminister(string id, ClaimsPrincipal user) => access.CanAdminister(user, id);

    /// <summary>Every workspace's agent, for work nobody asks for in a request (scheduled tasks).</summary>
    public IEnumerable<Agent> All() => Ids().Select(AgentOf);

    Agent AgentOf(string id) => agents.GetOrAdd(id, _ => new Agent(new Workspace(many ? Path.Combine(root, id) : root).Initialize(), host));

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in agents.Values) await agent.DisposeAsync();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex Id();
}
