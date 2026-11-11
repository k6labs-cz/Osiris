using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace Osiris.Core;

/// <summary>The agent's tools over the workspace folder.</summary>
public sealed class FileTools(Workspace workspace) : IToolProvider
{
    IEnumerable<AITool> IToolProvider.GetTools() => All();

    const int MaxReadChars = 100_000;
    const int MaxSearchHits = 100;

    public IList<AITool> All() =>
    [
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(WriteFile, "write_file"),
        AIFunctionFactory.Create(SearchFiles, "search_files"),
    ];

    [Description("Lists files and folders (folders end with /) in a workspace folder.")]
    public string ListFiles([Description("Folder relative to the workspace root")] string path = ".")
    {
        var dir = new DirectoryInfo(workspace.Resolve(path));
        var entries = dir.EnumerateFileSystemInfos()
            .Where(e => !e.Name.StartsWith('.'))
            .OrderBy(e => e.Name)
            .Select(e => workspace.Relative(e.FullName) + (e is DirectoryInfo ? "/" : ""));
        return string.Join('\n', entries) is { Length: > 0 } list ? list : "(empty)";
    }

    [Description("Reads a text file of the workspace.")]
    public string ReadFile([Description("File path relative to the workspace root")] string path)
    {
        var text = File.ReadAllText(workspace.Resolve(path));
        return text.Length > MaxReadChars ? text[..MaxReadChars] + "\n…(truncated)" : text;
    }

    [Description("Creates or overwrites a text file of the workspace.")]
    public string WriteFile([Description("File path relative to the workspace root")] string path, [Description("The whole new content")] string content)
    {
        var full = workspace.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return $"Saved {workspace.Relative(full)} ({content.Length} characters).";
    }

    [Description("Finds lines containing the text (case-insensitive) in the workspace's files.")]
    public string SearchFiles([Description("Text to find")] string query, [Description("Folder to search in")] string path = ".")
    {
        var hits = new StringBuilder();
        var count = 0;
        foreach (var file in Files(workspace.Resolve(path)))
        {
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                hits.AppendLine($"{workspace.Relative(file)}:{lineNo}: {line.Trim()}");
                if (++count == MaxSearchHits) return hits.Append("…(more results)").ToString();
            }
        }
        return count == 0 ? "No matches." : hits.ToString();
    }

    /// <summary>Files under a folder, skipping hidden ones (.osiris, .git) at any depth.</summary>
    static IEnumerable<string> Files(string dir)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos().Where(e => !e.Name.StartsWith('.')))
        {
            if (entry is DirectoryInfo) foreach (var f in Files(entry.FullName)) yield return f;
            else yield return entry.FullName;
        }
    }
}
