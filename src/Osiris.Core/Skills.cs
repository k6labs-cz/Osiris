using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Osiris.Core;

public sealed record Skill(string Name, string Description, string Instructions);

/// <summary>
/// A skill is a named set of instructions in <c>&lt;name&gt;/SKILL.md</c> (frontmatter with name and description, then
/// Markdown). Only name and description go into the prompt; the model loads the instructions with <c>load_skill</c>.
/// The workspace's skills (<c>.osiris/skills/</c>) win over the bundled ones shipped with the app of the same name.
/// Files are read on every run, so an edited skill applies from the next one.
/// </summary>
public sealed partial class Skills(Workspace workspace) : IToolProvider
{
    IEnumerable<AITool> IToolProvider.GetTools() => Tools();
    string IToolProvider.GetPrompt() => Prompt();

    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "skills");

    readonly string dir = workspace.DataOf("skills");

    /// <summary>The skills, one per name.</summary>
    public IReadOnlyList<Skill> List() =>
        new[] { dir, BundledDir }.SelectMany(Read).DistinctBy(s => s.Name).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    /// <summary>A malformed SKILL.md is skipped, so one bad file doesn't take the others down.</summary>
    static IEnumerable<Skill> Read(string dir) => Directory.Exists(dir)
        ? Directory.EnumerateDirectories(dir)
            .Select(d => (Folder: Path.GetFileName(d), File: Path.Combine(d, "SKILL.md")))
            .Where(d => File.Exists(d.File))
            .Select(d => Parse(File.ReadAllText(d.File)) is { } s && s.Name == d.Folder ? s : null)
            .OfType<Skill>()
        : [];

    public static Skill? Parse(string text)
    {
        var match = Frontmatter().Match(text);
        if (!match.Success) return null;
        var meta = match.Groups[1].Value.Split('\n')
            .Select(l => l.Split(':', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim().Trim('"', '\''));
        var skill = new Skill(meta.GetValueOrDefault("name", ""), meta.GetValueOrDefault("description", ""), match.Groups[2].Value.Trim());
        return Name().IsMatch(skill.Name) && skill.Description.Length > 0 && skill.Instructions.Length > 0 ? skill : null;
    }

    static string Format(Skill s) => $"---\nname: {s.Name}\ndescription: {WhiteSpace().Replace(s.Description, " ").Trim()}\n---\n\n{s.Instructions.Trim()}\n";

    /// <summary>Whether the workspace has its own skill of this name (a bundled one can't be deleted, only replaced).</summary>
    public bool IsOwn(string name) => Name().IsMatch(name) && File.Exists(Path.Combine(dir, name, "SKILL.md"));

    /// <summary>Deletes the workspace's own skill; a bundled one of the same name comes back.</summary>
    public bool Delete(string name)
    {
        if (!IsOwn(name)) return false;
        Directory.Delete(Path.Combine(dir, name), recursive: true);
        return true;
    }

    public string Prompt()
    {
        var list = List();
        if (list.Count == 0) return "";
        return $"""
            # SKILLS
            When a task matches a skill, first load its instructions with load_skill, then follow them.
            {string.Join('\n', list.Select(s => $"- {s.Name}: {s.Description}"))}
            """;
    }

    public IList<AITool> Tools() => [AIFunctionFactory.Create(LoadSkill, "load_skill"), AIFunctionFactory.Create(SaveSkill, "save_skill")];

    [Description("Loads the instructions of a skill from the SKILLS list. Call it before starting such a task.")]
    public string LoadSkill([Description("The skill's name")] string name) =>
        List().FirstOrDefault(s => s.Name == name)?.Instructions ?? $"There is no skill \"{name}\".";

    [Description("Saves a new skill, or overwrites the one of the same name. Save only what the user approved.")]
    public string SaveSkill(
        [Description("Name in kebab-case without diacritics, e.g. issue-invoice")] string name,
        [Description("One sentence: what the skill does and when to use it (the model picks skills by it)")] string description,
        [Description("The instructions in Markdown: steps, rules, examples")] string instructions)
    {
        // The name becomes a folder name, so the pattern also keeps it inside the skills folder.
        if (!Name().IsMatch(name)) return $"Error: \"{name}\" is not a valid name: lowercase letters, digits and dashes only (e.g. issue-invoice).";
        if (description.Trim().Length is 0 or > 300) return "Error: the description has 1 to 300 characters.";
        if (instructions.Trim().Length is 0 or > 50_000) return "Error: the instructions have 1 to 50 000 characters.";
        var file = Path.Combine(dir, name, "SKILL.md");
        var existed = File.Exists(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Format(new Skill(name, description.Trim(), instructions.Trim())));
        return $"Skill \"{name}\" {(existed ? "updated" : "saved")}. It's in the SKILLS list from the next message.";
    }

    [GeneratedRegex(@"^---\r?\n(.*?)\r?\n---\r?\n?(.*)$", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Name();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
