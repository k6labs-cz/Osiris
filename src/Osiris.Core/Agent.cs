using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Osiris.Core;

/// <summary>
/// The agent of one workspace: a ReAct loop of its own (model → tools → model … until it answers), so every step can be
/// shaped (prompt, tools, guards, stopping); each thread's history is kept on disk. The same agent runs in the
/// terminal, the desktop app and the server: a host only decides <see cref="AgentOptions"/> and shows the events.
/// </summary>
public sealed class Agent : IAsyncDisposable
{
    /// <summary>Model calls per turn; a model stuck calling tools ends the turn with an error.</summary>
    public const int MaxSteps = 40;

    readonly ConcurrentDictionary<Guid, SemaphoreSlim> running = new();
    readonly IChatClient model;
    readonly IToolProvider[] builtIn;

    /// <summary>
    /// An agent over a given model, whatever the configuration says (tests, hosts with a model of their own).
    /// <paramref name="secretKey"/> is the 32-byte key sealing the workspace's secrets, kept outside the workspace;
    /// <paramref name="allowPrivateUrls"/> is for an agent on the user's own machine (see <see cref="AgentOptions.Local"/>).
    /// </summary>
    public Agent(Workspace workspace, IChatClient model, IEmbeddingGenerator<string, Embedding<float>> embeddings, byte[] secretKey, bool allowPrivateUrls = false)
        : this(workspace, new AgentOptions { SecretKey = secretKey, Local = allowPrivateUrls, AllowedPackages = allowPrivateUrls ? ["*"] : [] }, model, embeddings) { }

    /// <summary>
    /// The agent as the workspace's <c>.osiris/osiris.json</c> configures it: its model and its packages.
    /// A host with a model of its own passes <paramref name="chat"/> and <paramref name="embeddings"/> instead.
    /// </summary>
    public Agent(Workspace workspace, AgentOptions host, IChatClient? chat = null, IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        Workspace = workspace;
        Secrets = new(workspace, host.SecretKey);
        var models = new Models(workspace, host, Secrets);
        model = chat ?? models.Chat;
        Threads = new(workspace);
        Memories = new(workspace);
        Skills = new(workspace);
        Knowledge = new(workspace, embeddings ?? models.Embeddings);
        Questions = new(workspace);
        Mcp = new(workspace, host.Local, Secrets);
        Guards = new(workspace, model);
        Schedules = new(workspace);
        Packages = new(workspace, host);
        Workflows = new(workspace, model, Questions, StepAsync);
        // The tools the agent can't do without are part of it; anything else comes as a package.
        builtIn = [new FileTools(workspace), Memories, Skills, Knowledge, new HttpTool(Secrets, host.Local), Schedules];
    }

    public Workspace Workspace { get; }
    public ThreadStore Threads { get; }
    public Memories Memories { get; }
    public Skills Skills { get; }
    public Knowledge Knowledge { get; }
    public Questions Questions { get; }
    public Secrets Secrets { get; }
    public Mcp Mcp { get; }
    public Guards Guards { get; }
    public Schedules Schedules { get; }
    public Packages Packages { get; }
    public Workflows Workflows { get; }

    /// <summary>A workflow step's own run gives up after this long, so one stuck model call can't hold the whole workflow.</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);

    /// <summary>What one run works with: the built-in and the packages' tools and guards, as configured right now.</summary>
    sealed record Setup(IList<AITool> Tools, IReadOnlyList<IGuard> Guards, string Prompts);

    // The agent authorizes nothing: who may run what is the host's concern.
    async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var extensions = await Packages.LoadAsync(ct);
        IToolProvider[] providers = [.. builtIn, .. extensions.Tools];
        IList<AITool> tools = [.. providers.SelectMany(p => p.GetTools()), .. await Mcp.ToolsAsync(extensions.McpServers, ct)];
        IGuard[] guards = [Guards, .. extensions.Guards];
        var prompts = string.Join("\n\n", new[] { Guards.Prompt() }.Concat(extensions.Guards.Select(g => g.GetPrompt())).Concat(providers.Select(p => p.GetPrompt())).Where(p => p.Length > 0));
        return new Setup(tools, guards, prompts);
    }

    public ValueTask DisposeAsync() => Mcp.DisposeAsync();

    /// <summary>Runs one turn and streams its events; a failure ends the stream with <see cref="RunError"/>.</summary>
    public async IAsyncEnumerable<RunEvent> RunAsync(Guid thread, string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // C# can't yield inside try/catch, so the inner stream is stepped by hand.
        await using var events = RunCoreAsync(thread, message, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            RunEvent next;
            try
            {
                if (!await events.MoveNextAsync()) break;
                next = events.Current;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                next = new RunError(e.Message);
            }
            yield return next;
            if (next is RunError) yield break;
        }
        yield return new Done();
    }

    async IAsyncEnumerable<RunEvent> RunCoreAsync(Guid thread, string message, [EnumeratorCancellation] CancellationToken ct)
    {
        // One run per thread at a time, so two runs never overwrite each other's history.
        var gate = running.GetOrAdd(thread, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var history = Threads.Load(thread);
            // While a question is open, the message answers it; one that doesn't fit ends the run here, unsaved.
            var text = Questions.Prepare(thread, message);
            history.Add(new ChatMessage(ChatRole.User, text));
            var setup = await SetupAsync(ct);
            if (await BreaksAsync(setup.Guards, new GuardRequest(GuardStage.UserMessage, text), ct) is not null)
            {
                // The agent never acts on a message breaking the rules.
                history.Add(new ChatMessage(ChatRole.Assistant, Guards.Refusal));
                yield return new TextDelta(Guards.Refusal);
            }
            else
            {
                await foreach (var e in LoopAsync(history, setup with { Tools = [.. setup.Tools, Workflows.Tool(thread), Questions.Tool(thread)] }, ct, thread)) yield return e;
            }
            Threads.Save(thread, history);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs the scheduled tasks whose time has come, each as a new thread, all at once; returns when they're done.</summary>
    public Task RunDueSchedulesAsync(DateTimeOffset now, CancellationToken ct = default) =>
        Task.WhenAll(Schedules.TakeDue(now).Select(async s =>
        {
            var thread = Guid.NewGuid();
            string? error = null;
            await foreach (var e in RunAsync(thread, s.Prompt, ct)) if (e is RunError failed) error = failed.Message;
            Schedules.Finished(s.Id, thread, error);
        }));

    /// <summary>A workflow step: a run of its own over a fresh history, with the agent's tools but no workflow of its own.</summary>
    async Task<string> StepAsync(string goal, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StepTimeout);
        List<ChatMessage> history = [new(ChatRole.User, goal)];
        try
        {
            await foreach (var _ in LoopAsync(history, await SetupAsync(timeout.Token), timeout.Token)) { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The step didn't finish within {StepTimeout.TotalMinutes} minutes.");
        }
        return history.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? "";
    }

    /// <summary>
    /// The ReAct loop: model → tools → model … until it answers, or until a tool asked the user something (the
    /// <paramref name="thread"/>'s question is open). Every message is added to <paramref name="history"/>.
    /// In a chat (<paramref name="thread"/> given) the answer and questions are checked against the rules first.
    /// </summary>
    async IAsyncEnumerable<RunEvent> LoopAsync(List<ChatMessage> history, Setup setup, [EnumeratorCancellation] CancellationToken ct, Guid? thread = null)
    {
        var tools = setup.Tools;
        // Built on every run, not kept in the thread, so the date, memory and skills are always current.
        var options = new ChatOptions { Instructions = Instructions(setup.Prompts), Tools = tools };
        // With rules, an answer is shown only once it's checked: its text comes whole, not streamed.
        var guarded = thread is not null && (Guards.List().Count > 0 || setup.Guards.Count > 1);

        for (var step = 0; ; step++)
        {
            if (step == MaxSteps) throw new InvalidOperationException($"The agent didn't answer within {MaxSteps} steps.");

            // agent: one model call, its text streamed as it comes
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in model.GetStreamingResponseAsync(history, options, ct))
            {
                updates.Add(update);
                if (guarded) continue;
                foreach (var t in update.Contents.OfType<TextContent>())
                    if (t.Text.Length > 0) yield return new TextDelta(t.Text);
            }
            var answer = updates.ToChatResponse();
            var calls = answer.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();

            if (guarded && calls.Count == 0 && await BreaksAsync(setup.Guards, new GuardRequest(GuardStage.AssistantAnswer, answer.Text), ct) is not null)
            {
                // Replaced in the history too, so the model never builds on it.
                history.Add(new ChatMessage(ChatRole.Assistant, Guards.Refusal));
                yield return new TextDelta(Guards.Refusal);
                break;
            }
            if (guarded && answer.Text.Length > 0) yield return new TextDelta(answer.Text);
            history.AddRange(answer.Messages);
            if (calls.Count == 0) break;

            // tools: run each call and hand its result back to the model
            var results = new List<AIContent>();
            var asksAlongside = calls.Count > 1 && calls.Any(c => c.Name == "ask_user");
            foreach (var call in calls)
            {
                yield return new ToolCall(call.CallId, call.Name, call.Arguments);
                // A question waits for one answer, so it can't come with other actions.
                var result = asksAlongside ? "A question must be asked alone. Nothing was done."
                    : await BreaksAsync(setup.Guards, new GuardRequest(GuardStage.ToolCall, JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions), call.Name, call.Arguments?.AsReadOnly()), ct) is { } why
                        ? $"Refused: {why} Nothing was done."
                    : await InvokeAsync(tools, call, ct);
                // A question a tool put to the user (ask_user, a workflow's approval) is checked like an answer.
                if (guarded && Questions.Open(thread!.Value) is { } asked
                    && await BreaksAsync(setup.Guards, new GuardRequest(GuardStage.AssistantAnswer, JsonSerializer.Serialize(new { asked.Title, asked.Fields })), ct) is not null)
                {
                    Questions.Withdraw(thread.Value, asked.Id);
                    result = "The question breaks the rules and wasn't asked. Nothing was done.";
                }
                yield return new ToolResult(call.CallId, ResultText(result));
                results.Add(new FunctionResultContent(call.CallId, result));
            }
            history.Add(new ChatMessage(ChatRole.Tool, results));
            if (thread is { } asking && Questions.Open(asking) is not null) break;
        }
    }

    /// <summary>
    /// Why the guards stop the request, or null when all let it pass. A guard that throws lets it through:
    /// a broken check mustn't take the agent down.
    /// </summary>
    static async Task<string?> BreaksAsync(IReadOnlyList<IGuard> guards, GuardRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) return null;
        foreach (var guard in guards)
        {
            try
            {
                if (await guard.CheckAsync(request, ct) is { Allowed: false } verdict) return verdict.Reason ?? "A guard refused it.";
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        return null;
    }

    /// <summary>A failing tool returns its error, so the model sees why (a path outside the workspace, a missing file) and can try again.</summary>
    static async Task<object?> InvokeAsync(IList<AITool> tools, FunctionCallContent call, CancellationToken ct)
    {
        if (tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == call.Name) is not { } tool) return $"Error: no tool named {call.Name}.";
        try
        {
            return await tool.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Error: {e.Message}";
        }
    }

    string Instructions(string prompts) => $"""
        You are Osiris, an assistant working in the workspace "{Workspace.Name}".
        Today is {DateTimeOffset.Now:dddd yyyy-MM-dd HH:mm zzz} ({TimeZoneInfo.Local.Id}).
        The workspace's files are yours to read and edit with your tools; paths are relative to the workspace folder.
        Answer in the language of the user. Be brief and concrete.
        When key information is missing, ask with ask_user (a question, a choice, a confirmation or a short form). Call it alone
        and wait for the answer. A cancelled form or a no is not consent.

        {prompts}

        {Secrets.Prompt()}

        {Mcp.Prompt()}

        {Packages.Prompt()}
        """;

    static string ResultText(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement j when j.ValueKind == JsonValueKind.String => j.GetString()!,
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions),
    };
}
