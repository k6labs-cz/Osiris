using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Osiris.Core;

public sealed record ThreadInfo(Guid Id, string Title, DateTime Updated);

/// <summary>The workspace's conversations: one JSON file of messages per thread in <c>.osiris/threads/</c>.</summary>
public sealed class ThreadStore(Workspace workspace)
{
    static readonly JsonSerializerOptions Json = AIJsonUtilities.DefaultOptions;

    readonly string dir = workspace.DataOf("threads");

    string FileOf(Guid id) => Path.Combine(dir, $"{id}.json");

    public IReadOnlyList<ThreadInfo> List()
    {
        if (!Directory.Exists(dir)) return [];
        // shortcut: reads every thread for its title, fine for hundreds of threads; keep an index file beyond that.
        return new DirectoryInfo(dir).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new ThreadInfo(Guid.Parse(Path.GetFileNameWithoutExtension(f.Name)), TitleOf(Load(f.FullName)), f.LastWriteTimeUtc))
            .ToList();
    }

    public List<ChatMessage> Load(Guid id) => Load(FileOf(id));

    static List<ChatMessage> Load(string file) =>
        File.Exists(file) ? JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(file), Json) ?? [] : [];

    /// <summary>Writes a temp file and renames it, so a crash never leaves half a thread.</summary>
    public void Save(Guid id, List<ChatMessage> messages)
    {
        Directory.CreateDirectory(dir);
        var temp = FileOf(id) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(messages, Json));
        File.Move(temp, FileOf(id), overwrite: true);
    }

    public void Delete(Guid id) => File.Delete(FileOf(id));

    static string TitleOf(List<ChatMessage> messages)
    {
        var first = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text.ReplaceLineEndings(" ") ?? "";
        return first.Length > 60 ? first[..60] + "…" : first is "" ? "New chat" : first;
    }
}
