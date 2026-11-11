using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Osiris.Core;

public sealed record PlanStep(
    [property: Description("Short kebab-case id of the step")] string Id,
    [property: Description("What the step does, understandable on its own")] string Goal,
    [property: Description("ReAct, JEV, Reflexion or MapReduce")] string Pattern,
    [property: Description("Ids of the steps whose results this step needs")] string[] DependsOn,
    [property: Description("Changes or deletes data (writes a file, sends something)")] bool Destructive,
    [property: Description("JEV: the name of the check of the result")] string? Verifier = null,
    [property: Description("MapReduce: the items to process (file paths, names)")] string[]? Items = null);

public sealed record RoutePlan([property: Description("Why this plan, in one sentence")] string Rationale, PlanStep[] Steps);

public enum StepStatus { Success, Failed, Rejected, Skipped }

public sealed record StepResult(StepStatus Status, string Output);

/// <summary>
/// The orchestrator for larger tasks (<c>run_workflow</c>): a router plans the request into steps, each run as a pattern
/// (ReAct: the agent itself; JEV: a checked artifact; Reflexion: an artifact a critic accepts; MapReduce: the same work
/// per item), in waves of the steps whose dependencies succeeded, then a summary answers. A plan with steps that
/// change data waits in <c>.osiris/workflows/&lt;thread&gt;.json</c> while the user approves them in a question.
/// </summary>
public sealed partial class Workflows(Workspace workspace, IChatClient model, Questions questions, Func<string, CancellationToken, Task<string>> react)
{
    public const int MaxPlanSteps = 8;
    public const int MaxMapItems = 50;
    public const int MapConcurrency = 4;
    public const int MaxRepairs = 3;
    /// <summary>A dependency's output given to the next step, cut so a chain of steps stays within the context.</summary>
    // shortcut: plain truncation, summarize outputs when long chains need the whole of them.
    const int MaxContextChars = 4000;

    static readonly string[] Patterns = ["ReAct", "JEV", "Reflexion", "MapReduce"];

    /// <summary>Deterministic checks of a JEV artifact: the problems found, none when it's valid.</summary>
    // shortcut: JSON only; the TS agent also checks JSON-LD, DTDL and SQL, add them with the features that need them.
    public static readonly IReadOnlyDictionary<string, Func<string, string[]>> Verifiers = new Dictionary<string, Func<string, string[]>>
    {
        ["json"] = artifact =>
        {
            try { using var _ = JsonDocument.Parse(artifact); return []; }
            catch (JsonException e) { return [$"Invalid JSON: {e.Message}"]; }
        },
    };

    readonly string dir = workspace.DataOf("workflows");

    /// <summary>The <c>run_workflow</c> tool of one thread (a waiting plan belongs to its thread).</summary>
    public AIFunction Tool(Guid thread) => AIFunctionFactory.Create(
        ([Description("The whole task, understandable on its own")] string request,
         [Description("Go on with the workflow that waited for the user's approval (request may then be empty)")] bool resume = false,
         CancellationToken ct = default) => RunAsync(thread, request, resume, ct),
        "run_workflow",
        "Carries out a larger task as a managed procedure: plans it into steps, checks and repairs generated JSON, goes through a " +
        "collection item by item, and asks the user to approve steps that change data. Use it only for multi-step tasks or " +
        "machine-checkable results, not for simple questions. Call it alone. When the user answered the approval, call it again with resume: true.");

    sealed record Pending(RoutePlan Plan, Guid Question);

    public async Task<string> RunAsync(Guid thread, string request, bool resume, CancellationToken ct = default)
    {
        var pendingFile = Path.Combine(dir, $"{thread}.json");
        RoutePlan plan;
        HashSet<string> approved = [];
        if (resume)
        {
            if (!File.Exists(pendingFile)) return "No workflow waits for approval.";
            var pending = JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(pendingFile, ct), AIJsonUtilities.DefaultOptions)!;
            plan = pending.Plan;
            // The decision is the user's answer to the approval question, never the model's word for it.
            if (questions.Open(thread)?.Id == pending.Question) return "The user hasn't answered the approval yet. Wait for their answer.";
            if (questions.Take(thread, pending.Question) is not { } decision)
                return await AskApprovalAsync(thread, pendingFile, plan, ct); // replaced or never answered: ask again
            File.Delete(pendingFile);
            var destructive = plan.Steps.Where(s => s.Destructive).ToList();
            approved = [.. destructive.Where((s, i) => decision.TryGetValue(Field(i), out var v) && v.ValueKind == JsonValueKind.True).Select(s => s.Id)];
        }
        else
        {
            plan = await RouteAsync(request, ct);
            if (plan.Steps.Any(s => s.Destructive)) return await AskApprovalAsync(thread, pendingFile, plan, ct);
        }

        var results = await ExecuteAsync(plan.Steps, approved, ct);
        var answer = await CompleteAsync(request, plan.Steps, results, ct);
        return $"{answer}\n\n(Workflow steps: {string.Join(", ", plan.Steps.Select(s => $"{s.Id}: {results[s.Id].Status.ToString().ToLowerInvariant()}"))})";
    }

    static string Field(int i) => $"step_{i + 1}";

    /// <summary>One yes/no per step that changes data, as a question in the chat; the plan waits beside it.</summary>
    async Task<string> AskApprovalAsync(Guid thread, string pendingFile, RoutePlan plan, CancellationToken ct)
    {
        var fields = plan.Steps.Where(s => s.Destructive).Select((s, i) => new QuestionField(Field(i), s.Goal.Length > 150 ? s.Goal[..150] : s.Goal, "boolean", true)).ToArray();
        var question = questions.Create(thread, "Approve the steps that change data?", fields);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(pendingFile, JsonSerializer.Serialize(new Pending(plan, question.Id), AIJsonUtilities.DefaultOptions), ct);
        return JsonSerializer.Serialize(new { question, plan = plan.Steps.Select(s => $"{s.Id}{(s.Destructive ? " [changes data]" : "")}: {s.Goal}") }, AIJsonUtilities.DefaultOptions);
    }

    /// <summary>The router's plan; one it got wrong falls back to a single ReAct step, which handles anything.</summary>
    async Task<RoutePlan> RouteAsync(string request, CancellationToken ct)
    {
        try
        {
            var route = await StructuredAsync<RoutePlan>($"""
                You are a planner. Split the user's request into steps and pick a pattern for each:
                - ReAct: research, answers, work with tools (files, knowledge, memory). A simple request is one ReAct step.
                - JEV: the result is a machine-checkable artifact (JSON). Give the verifier: {string.Join(", ", Verifiers.Keys)}.
                - Reflexion: a text result that can't be checked by a machine but must meet exact criteria (a proposal, a contract, an offer).
                - MapReduce: the same work over many items (files); list them in items.
                Link steps with dependsOn. Mark a step that changes or deletes data or sends something as destructive. At most {MaxPlanSteps} steps.
                """, request, ct);
            return route with { Steps = Validate(route.Steps) };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new RoutePlan($"The plan couldn't be made ({e.Message}), the agent handles it.", [new PlanStep("task", request, "ReAct", [], false)]);
        }
    }

    /// <summary>Unique ids, known dependencies, items for MapReduce, no cycles; returns the steps in dependency order.</summary>
    public static PlanStep[] Validate(PlanStep[] steps)
    {
        if (steps.Length is 0 or > MaxPlanSteps) throw new InvalidOperationException($"A plan has 1 to {MaxPlanSteps} steps.");
        var ids = steps.Select(s => s.Id).ToHashSet();
        if (ids.Count != steps.Length) throw new InvalidOperationException("Duplicate step ids.");
        foreach (var s in steps)
        {
            if (!StepId().IsMatch(s.Id)) throw new InvalidOperationException($"Invalid step id {s.Id}.");
            if (!Patterns.Contains(s.Pattern)) throw new InvalidOperationException($"Step {s.Id} has an unknown pattern {s.Pattern}.");
            if (s.DependsOn.FirstOrDefault(d => !ids.Contains(d)) is { } unknown) throw new InvalidOperationException($"Step {s.Id} depends on unknown step {unknown}.");
            if (s.Pattern == "MapReduce" && s.Items is not { Length: > 0 and <= MaxMapItems }) throw new InvalidOperationException($"MapReduce step {s.Id} needs 1 to {MaxMapItems} items.");
        }
        var ordered = new List<PlanStep>();
        while (ordered.Count < steps.Length)
        {
            var next = steps.Where(s => !ordered.Contains(s) && s.DependsOn.All(d => ordered.Any(o => o.Id == d))).ToList();
            if (next.Count == 0) throw new InvalidOperationException("The plan has a dependency cycle.");
            ordered.AddRange(next);
        }
        return [.. ordered];
    }

    /// <summary>
    /// Waves of the ready steps (all dependencies succeeded), each wave in parallel. A destructive step the user didn't
    /// approve is rejected; a failing step fails alone; steps after either are skipped.
    /// </summary>
    async Task<Dictionary<string, StepResult>> ExecuteAsync(PlanStep[] plan, HashSet<string> approved, CancellationToken ct)
    {
        var results = new Dictionary<string, StepResult>();
        while (plan.Where(s => !results.ContainsKey(s.Id) && s.DependsOn.All(d => results.GetValueOrDefault(d)?.Status == StepStatus.Success)).ToList() is { Count: > 0 } wave)
        {
            foreach (var s in wave.Where(s => s.Destructive && !approved.Contains(s.Id))) results[s.Id] = new(StepStatus.Rejected, "The user didn't approve the step.");
            var done = await Task.WhenAll(wave.Where(s => !results.ContainsKey(s.Id)).Select(async s =>
            {
                try { return (s.Id, Result: await RunStepAsync(s, Brief(s, results), ct)); }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return (s.Id, Result: new StepResult(StepStatus.Failed, e.Message)); }
            }));
            foreach (var (id, result) in done) results[id] = result;
        }
        foreach (var s in plan.Where(s => !results.ContainsKey(s.Id))) results[s.Id] = new(StepStatus.Skipped, "Not run: an earlier step didn't succeed.");
        return results;
    }

    static string Brief(PlanStep step, Dictionary<string, StepResult> results)
    {
        var inputs = step.DependsOn.Select(d => $"- {d}: {Cut(results[d].Output)}").ToList();
        return inputs.Count == 0 ? "" : $"Results of the steps this step depends on:\n{string.Join('\n', inputs)}";
        static string Cut(string s) => s.Length > MaxContextChars ? s[..MaxContextChars] : s;
    }

    async Task<StepResult> RunStepAsync(PlanStep step, string context, CancellationToken ct)
    {
        var goal = context.Length > 0 ? $"{step.Goal}\n\n{context}" : step.Goal;
        return step.Pattern switch
        {
            "ReAct" => new(StepStatus.Success, await react(goal, ct)),
            "MapReduce" => new(StepStatus.Success, await MapReduceAsync(goal, step.Items!, ct)),
            _ => await JudgeExecuteVerifyAsync(step, goal, ct),
        };
    }

    /// <summary>Every item as a ReAct run of its own, a few at once; one broken item doesn't sink the rest. Then one summary.</summary>
    async Task<string> MapReduceAsync(string goal, string[] items, CancellationToken ct)
    {
        var partials = new string[items.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Length), new ParallelOptions { MaxDegreeOfParallelism = MapConcurrency, CancellationToken = ct }, async (i, token) =>
        {
            try { partials[i] = await react($"{goal}\n\nProcess only this item: {items[i]}", token); }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested) { partials[i] = $"ERROR: {e.Message}"; }
        });
        return await TextAsync("Sum up the partial findings into one result of the task. Leave nothing out and make nothing up; list the failed items.",
            $"Task: {goal}\n\n{string.Join("\n\n", items.Select((item, i) => $"## {item}\n{partials[i]}"))}", ct);
    }

    sealed record Criteria(string[] Items);
    sealed record Evaluation(bool Accepted, string Feedback);

    /// <summary>
    /// JEV: a judge sets the criteria, then each attempt is written, checked by the verifier (JEV) and evaluated against
    /// the criteria; what was wrong goes to the next attempt. Reflexion is the same without the verifier.
    /// </summary>
    async Task<StepResult> JudgeExecuteVerifyAsync(PlanStep step, string goal, CancellationToken ct)
    {
        var brief = $"Task: {goal}";
        var criteria = (await StructuredAsync<Criteria>("You are a judge. Before the result exists, set exact, checkable criteria it must meet (1 to 8). Don't write the result.", brief, ct)).Items;
        var listed = string.Join('\n', criteria.Select(c => $"- {c}"));
        var reflections = new List<string>();
        for (var attempt = 1; attempt <= 1 + MaxRepairs; attempt++)
        {
            var memory = reflections.Count == 0 ? "" : $"\n\nEarlier attempts were rejected, don't repeat these mistakes:\n{string.Join('\n', reflections.Select(r => $"- {r}"))}";
            var artifact = await TextAsync("Create the requested result (code, model, query, schema, text). Answer with the result only, without explanation.",
                $"{brief}\n\nCriteria:\n{listed}{memory}", ct);

            if (step.Pattern == "JEV" && step.Verifier is { } name)
            {
                var errors = Verifiers.TryGetValue(name, out var check) ? check(artifact) : [$"Unknown check {name}"];
                if (errors.Length > 0) { reflections.Add($"Attempt {attempt} failed the {name} check: {string.Join("; ", errors)}"); continue; }
            }

            var verdict = await StructuredAsync<Evaluation>(
                "You are an evaluator. Judge the result only by the given criteria. Accept it when it meets them all; reject it only for a broken criterion, " +
                "not for style, formatting or possible improvements. In feedback, say which criterion is broken and how to fix it.",
                $"{brief}\n\nCriteria:\n{listed}\n\nResult:\n{artifact}", ct);
            if (verdict.Accepted) return new(StepStatus.Success, artifact);
            reflections.Add($"Attempt {attempt} rejected: {verdict.Feedback}");
        }
        return new(StepStatus.Failed, string.Join('\n', reflections));
    }

    /// <summary>The answer: a single step's own output, else a summary of the results that says plainly what didn't happen.</summary>
    async Task<string> CompleteAsync(string request, PlanStep[] plan, Dictionary<string, StepResult> results, CancellationToken ct)
    {
        if (plan.Length == 1)
        {
            var only = results[plan[0].Id];
            return only.Status == StepStatus.Success ? only.Output : $"The task couldn't be done: {only.Output}";
        }
        var report = string.Join("\n\n", plan.Select(s => $"## {s.Id} [{results[s.Id].Status.ToString().ToUpperInvariant()}]: {s.Goal}\n{results[s.Id].Output}"));
        var summary = await TextAsync(
            "Answer the user in the language of their request from the steps' results. Use only SUCCESS steps' results. For FAILED, REJECTED " +
            "or SKIPPED steps say clearly that they didn't happen and why; never pretend their result.",
            $"Request: {request}\n\n{report}", ct);
        // What didn't get done is stated by the workflow itself, whatever the summary makes of it.
        var missed = plan.Where(s => results[s.Id].Status != StepStatus.Success).Select(s => $"{s.Goal} ({results[s.Id].Status.ToString().ToLowerInvariant()})").ToList();
        return missed.Count == 0 ? summary : $"Not done: {string.Join("; ", missed)}.\n\n{summary}";
    }

    async Task<T> StructuredAsync<T>(string system, string user, CancellationToken ct) =>
        (await model.GetResponseAsync<T>([new(ChatRole.System, system), new(ChatRole.User, user)], cancellationToken: ct)).Result;

    /// <summary>A plain text answer, without the Markdown fence models like to wrap code in.</summary>
    async Task<string> TextAsync(string system, string user, CancellationToken ct)
    {
        var text = (await model.GetResponseAsync([new(ChatRole.System, system), new(ChatRole.User, user)], cancellationToken: ct)).Text.Trim();
        return Fence().Match(text) is { Success: true } m ? m.Groups[1].Value : text;
    }

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex StepId();

    [GeneratedRegex(@"^```[\w-]*\n([\s\S]*?)\n```$")]
    private static partial Regex Fence();
}
