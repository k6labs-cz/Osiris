using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Osiris.Core;
using Spectre.Console;

const string Help = """
    osiris [folder]             chat with the agent of the workspace the folder is in (default: here)
    osiris -p "message"         one answer, printed; for scripts
    osiris init [folder]        make the folder a workspace (.osiris/)

    In a chat: /new, /threads, /help, /quit.
    The model's key comes from OSIRIS_API_KEY (or MISTRAL_API_KEY, OPENAI_API_KEY); everything else from
    .osiris/osiris.json in the workspace and osiris.json in your configuration folder.
    """;

if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Help); return 0; }
if (args.Contains("--version"))
{
    Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]);
    return 0;
}

var prompt = Option("-p") ?? Option("--prompt");
var positional = args.Where((a, i) => !a.StartsWith('-') && (i == 0 || args[i - 1] is not ("-p" or "--prompt"))).ToList();

if (positional is ["init", ..])
{
    var made = new Workspace(positional.ElementAtOrDefault(1) ?? ".").Initialize();
    AnsiConsole.MarkupLineInterpolated($"Workspace ready: [aqua]{made.DataOf("osiris.json")}[/]");
    return 0;
}

// Like git: the workspace is the nearest folder upwards with .osiris/.
var start = Path.GetFullPath(positional.FirstOrDefault() ?? ".");
if (!Directory.Exists(start)) { AnsiConsole.MarkupLineInterpolated($"[red]No folder {start}.[/]"); return 1; }
var workspace = Workspace.Find(start);
if (workspace is null)
{
    if (prompt is not null || Console.IsInputRedirected || !AnsiConsole.Confirm($"No Osiris workspace here. Make [aqua]{Markup.Escape(start)}[/] one?"))
    {
        AnsiConsole.MarkupLine("[red]Not in an Osiris workspace.[/] Make one with [aqua]osiris init[/].");
        return 1;
    }
    workspace = new Workspace(start).Initialize();
}

await using var agent = new Agent(workspace, AgentOptions.ForUser());
using var stopping = new CancellationTokenSource();
var thread = Guid.NewGuid();

if (prompt is not null) return await RunAsync(prompt, plain: true) ? 0 : 1;

// Scheduled tasks run while a chat is open (unless another Osiris app over the workspace runs them already).
_ = AgentScheduler.RunAsync(() => [agent], ct: stopping.Token);

AnsiConsole.MarkupLine($"[bold]Osiris[/] · {Markup.Escape(workspace.Name)} · [grey]/new, /threads, /help, /quit[/]");
while (true)
{
    AnsiConsole.Markup("\n[aqua]>[/] ");
    var line = Console.ReadLine()?.Trim();
    switch (line)
    {
        case null or "/quit":
            stopping.Cancel();
            return 0;
        case "": continue;
        case "/help":
            Console.WriteLine(Help);
            continue;
        case "/new":
            thread = Guid.NewGuid();
            AnsiConsole.MarkupLine("[grey]New chat.[/]");
            continue;
        case "/threads":
            var threads = agent.Threads.List();
            if (threads.Count == 0) { AnsiConsole.MarkupLine("[grey]No chats yet.[/]"); continue; }
            thread = AnsiConsole.Prompt(new SelectionPrompt<ThreadInfo>().Title("Chat:").UseConverter(t => Markup.Escape(t.Title)).AddChoices(threads)).Id;
            PrintHistory(agent.Threads.Load(thread));
            continue;
        default:
            await RunAsync(line, plain: false);
            // A question the agent asked (ask_user, a workflow's approval) is answered in a form, as in the web UI.
            while (agent.Questions.Open(thread) is { } question) await RunAsync(Ask(question), plain: false);
            continue;
    }
}

async Task<bool> RunAsync(string message, bool plain)
{
    var ok = true;
    var atLineStart = true;
    await foreach (var e in agent.RunAsync(thread, message))
    {
        switch (e)
        {
            case TextDelta d:
                Console.Write(d.Delta);
                atLineStart = d.Delta.EndsWith('\n');
                break;
            case ToolCall c when !plain:
                if (!atLineStart) Console.WriteLine();
                AnsiConsole.MarkupLine($"[grey]⚙ {Markup.Escape(c.ToolName)}[/]");
                atLineStart = true;
                break;
            case RunError error:
                ok = false;
                if (plain) Console.Error.WriteLine(error.Message);
                else AnsiConsole.MarkupLine($"\n[red]{Markup.Escape(error.Message)}[/]");
                break;
        }
    }
    Console.WriteLine();
    return ok;
}

/// The question as prompts; returns the message that answers it.
static string Ask(Question question)
{
    AnsiConsole.MarkupLine($"\n[bold]{Markup.Escape(question.Title)}[/] [grey](empty answer to a required field cancels)[/]");
    var values = new Dictionary<string, object?>();
    foreach (var f in question.Fields)
    {
        var label = Markup.Escape(f.Label);
        var options = f.Options ?? [];
        switch (f.Type)
        {
            case "boolean":
                values[f.Name] = AnsiConsole.Confirm(label);
                break;
            case "select" when options.Length > 0:
                values[f.Name] = AnsiConsole.Prompt(new SelectionPrompt<QuestionOption>().Title(label).UseConverter(o => Markup.Escape(o.Label)).AddChoices(options)).Value;
                break;
            case "multiselect" when options.Length > 0:
                var multi = new MultiSelectionPrompt<QuestionOption>().Title(label).UseConverter(o => Markup.Escape(o.Label)).AddChoices(options);
                multi.Required = f.Required;
                values[f.Name] = AnsiConsole.Prompt(multi).Select(o => o.Value).ToArray();
                break;
            default:
                var text = AnsiConsole.Prompt(new TextPrompt<string>(label + (f.Required ? " *" : "")).AllowEmpty()).Trim();
                if (text.Length > 0) values[f.Name] = text;
                else if (f.Required) return Questions.AnswerPrefix + JsonSerializer.Serialize(new { id = question.Id, cancel = true });
                break;
        }
    }
    return Questions.AnswerPrefix + JsonSerializer.Serialize(new { id = question.Id, values });
}

static void PrintHistory(List<ChatMessage> messages)
{
    foreach (var m in messages)
    {
        foreach (var c in m.Contents)
        {
            if (c is TextContent { Text.Length: > 0 } t)
                AnsiConsole.MarkupLine(m.Role == ChatRole.User ? $"\n[aqua]>[/] {Markup.Escape(Questions.Display(t.Text))}" : Markup.Escape(t.Text));
            else if (c is FunctionCallContent f)
                AnsiConsole.MarkupLine($"[grey]⚙ {Markup.Escape(f.Name)}[/]");
        }
    }
}

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
