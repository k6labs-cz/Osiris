namespace Osiris.Core;

/// <summary>
/// A workspace is a folder marked by <c>.osiris/</c> inside it, the way <c>.git/</c> marks a repository: the agent's
/// configuration (<c>osiris.json</c>) and its own data live there.
/// </summary>
public sealed class Workspace(string root) : IWorkspace
{
    public const string Marker = ".osiris";

    /// <summary>
    /// What git must not keep of <c>.osiris/</c>: conversations and what belongs to them, the search index (rebuilt
    /// from the knowledge files), and what a running agent leaves behind. Configuration, rules, skills, memory and
    /// the sealed secrets stay versioned.
    /// </summary>
    const string GitIgnore = """
        # Osiris: only what's temporary or rebuilt. Configuration (osiris.json), guards/, skills/,
        # memory/, schedules.json and the sealed secrets/ are meant to be committed.
        threads/
        questions/
        workflows/
        run/
        knowledge.db
        *.db-shm
        *.db-wal
        *.tmp

        """;

    const string DefaultConfig = """
        {
          "$schema": "https://raw.githubusercontent.com/k6labs-cz/Osiris/main/schemas/osiris.schema.json"
        }

        """;

    public string Root { get; } = Path.GetFullPath(root);
    public string Name => Path.GetFileName(Root);

    /// <summary><c>.osiris/osiris.json</c>.</summary>
    public string ConfigFile => DataOf("osiris.json");

    /// <summary>Whether the folder is a workspace already.</summary>
    public bool Exists => Directory.Exists(DataOf(""));

    /// <summary>Osiris's own data of a kind (threads, memory, skills, …) in <c>.osiris/</c>.</summary>
    public string DataOf(string kind) => Path.Combine(Root, Marker, kind);

    /// <summary>The workspace <paramref name="start"/> is in: the nearest folder upwards with <c>.osiris/</c>, as git finds its repository.</summary>
    public static Workspace? Find(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, Marker))) return new Workspace(dir.FullName);
        return null;
    }

    /// <summary>Makes the folder a workspace: <c>.osiris/</c> with its <c>.gitignore</c> and an empty <c>osiris.json</c>. Existing files are left alone.</summary>
    public Workspace Initialize()
    {
        Directory.CreateDirectory(DataOf(""));
        if (!File.Exists(DataOf(".gitignore"))) File.WriteAllText(DataOf(".gitignore"), GitIgnore);
        if (!File.Exists(ConfigFile)) File.WriteAllText(ConfigFile, DefaultConfig);
        return this;
    }

    /// <summary>A path the agent may touch: inside the folder, with no hidden segment (.osiris, .git, …).</summary>
    public string Resolve(string path)
    {
        // shortcut: symlinks inside the folder are followed, refuse them if workspaces come from untrusted users.
        var full = Path.GetFullPath(Path.Combine(Root, path));
        var relative = Path.GetRelativePath(Root, full);
        if (relative == ".") return full;
        if (relative.StartsWith("..") || Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith('.')))
            throw new UnauthorizedAccessException($"Path '{path}' is outside the workspace.");
        return full;
    }

    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    /// <summary>
    /// Takes a lock only one process holds (<c>.osiris/run/&lt;name&gt;.lock</c>), or null when another has it:
    /// several apps may be open over one workspace, but its scheduled tasks must run once.
    /// </summary>
    public IDisposable? TryLock(string name)
    {
        try
        {
            Directory.CreateDirectory(DataOf("run"));
            return new FileStream(DataOf(Path.Combine("run", $"{name}.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
