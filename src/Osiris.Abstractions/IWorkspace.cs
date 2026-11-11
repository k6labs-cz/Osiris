namespace Osiris;

/// <summary>The folder an agent works over. Osiris keeps its own files in <c>.osiris/</c> inside it.</summary>
public interface IWorkspace
{
    /// <summary>The folder's full path.</summary>
    string Root { get; }

    /// <summary>The folder's name.</summary>
    string Name { get; }

    /// <summary>A file or folder of Osiris's own data in <c>.osiris/</c>, e.g. <c>DataOf("database.db")</c>. The agent's file tools can't reach it.</summary>
    string DataOf(string kind);

    /// <summary>
    /// The full path of a workspace-relative path the agent may touch: inside the folder, with no hidden segment
    /// (<c>.osiris</c>, <c>.git</c>, …). Throws <see cref="UnauthorizedAccessException"/> otherwise.
    /// Run every path that comes from the model through it.
    /// </summary>
    string Resolve(string path);

    /// <summary>A full path as the model sees it: relative to the root, with forward slashes.</summary>
    string Relative(string fullPath);
}
