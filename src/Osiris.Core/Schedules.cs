using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Cronos;
using Microsoft.Extensions.AI;

namespace Osiris.Core;

public sealed record Schedule(
    Guid Id, string Title, string Prompt, string? Cron, DateTimeOffset? RunAt, bool Enabled, DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt = null, string? LastStatus = null, string? LastError = null, Guid? LastThread = null);

/// <summary>
/// Tasks the agent runs by itself at set times (once, or repeating by cron), one list in <c>.osiris/schedules.json</c>.
/// Each run is a thread of its own, so its result is in the chat. Times are in the machine's time zone.
/// </summary>
public sealed class Schedules(Workspace workspace, TimeZoneInfo? zone = null) : IToolProvider
{
    IEnumerable<AITool> IToolProvider.GetTools() => Tools();

    public const int MaxSchedules = 50;
    readonly TimeZoneInfo zone = zone ?? TimeZoneInfo.Local;
    readonly string file = workspace.DataOf("schedules.json");
    readonly Lock gate = new();

    public IReadOnlyList<Schedule> List()
    {
        lock (gate) return Read();
    }

    public Schedule Create(string title, string prompt, string? cron, DateTimeOffset? runAt, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        title = title.Trim();
        prompt = prompt.Trim();
        if (title.Length is 0 or > 100) throw new ArgumentException("The title has 1 to 100 characters.");
        if (prompt.Length is 0 or > 4000) throw new ArgumentException("The prompt has 1 to 4000 characters.");
        if (cron is null == runAt is null) throw new ArgumentException("Give either cron (repeating) or run_at (once).");
        if (runAt <= at) throw new ArgumentException("run_at is in the past.");
        var schedule = new Schedule(Guid.NewGuid(), title, prompt, cron?.Trim(), runAt, true, null);
        schedule = schedule with { NextRunAt = Next(schedule, at) ?? throw new ArgumentException("The cron expression never fires.") };
        lock (gate)
        {
            var all = Read();
            if (all.Count >= MaxSchedules) throw new ArgumentException($"At most {MaxSchedules} scheduled tasks.");
            Write([.. all, schedule]);
        }
        return schedule;
    }

    public bool Delete(Guid id)
    {
        lock (gate)
        {
            var all = Read();
            if (all.RemoveAll(s => s.Id == id) == 0) return false;
            Write(all);
            return true;
        }
    }

    /// <summary>
    /// The enabled tasks whose time has come, each moved on to its next time first (a one-off is switched off), so a task
    /// runs once even if the runner stops halfway. Missed times (the app was down) run once, not once each.
    /// </summary>
    public IReadOnlyList<Schedule> TakeDue(DateTimeOffset now)
    {
        lock (gate)
        {
            var all = Read();
            var due = all.Where(s => s.Enabled && s.NextRunAt <= now).ToList();
            if (due.Count == 0) return [];
            Write([.. all.Select(s => due.Any(d => d.Id == s.Id) ? s with { NextRunAt = Next(s, now), Enabled = s.Cron is not null, LastRunAt = now } : s)]);
            return due;
        }
    }

    /// <summary>Records how a run ended, and the thread holding its result.</summary>
    public void Finished(Guid id, Guid thread, string? error)
    {
        lock (gate) Write([.. Read().Select(s => s.Id == id ? s with { LastStatus = error is null ? "done" : "failed", LastError = error, LastThread = thread } : s)]);
    }

    DateTimeOffset? Next(Schedule s, DateTimeOffset after)
    {
        if (s.Cron is null) return s.RunAt > after ? s.RunAt : null;
        CronExpression cron;
        try { cron = CronExpression.Parse(s.Cron); }
        catch (CronFormatException e) { throw new ArgumentException($"Invalid cron \"{s.Cron}\": {e.Message}"); }
        return cron.GetNextOccurrence(after, zone);
    }

    // ---- tools ----

    public IList<AITool> Tools() =>
    [
        AIFunctionFactory.Create(ScheduleTask, "schedule_task"),
        AIFunctionFactory.Create(ListSchedules, "list_schedules"),
        AIFunctionFactory.Create(CancelSchedule, "cancel_schedule"),
    ];

    [Description("Schedules a task the agent runs by itself at a set time, once or repeating. Only on the user's explicit instruction. " +
        "Write the prompt as a task on its own that makes sense without this conversation; its result appears as a new chat.")]
    public string ScheduleTask(
        [Description("A short name of the task")] string title,
        [Description("What the agent should do at that time")] string prompt,
        [Description("Repeating, as cron (5 fields, local time), e.g. \"0 7 * * 1-5\" = weekdays at 7:00")] string? cron = null,
        [Description("Once, ISO 8601 with a time zone, e.g. 2026-10-20T07:00:00+02:00")] string? run_at = null)
    {
        DateTimeOffset? at = null;
        if (run_at is not null)
        {
            if (!DateTimeOffset.TryParse(run_at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return $"Error: run_at \"{run_at}\" isn't an ISO 8601 time.";
            at = parsed;
        }
        try
        {
            var s = Create(title, prompt, cron, at);
            return $"Scheduled: \"{s.Title}\" (id {s.Id}), first {When(s.NextRunAt)}{(s.Cron is null ? "" : $", repeating {s.Cron}")}.";
        }
        catch (ArgumentException e)
        {
            return $"Error: {e.Message}";
        }
    }

    [Description("Lists the workspace's scheduled tasks.")]
    public string ListSchedules()
    {
        var list = List();
        return list.Count == 0 ? "No scheduled tasks." : string.Join('\n', list.Select(s =>
            $"- [{s.Id}] {s.Title}: {(s.Enabled ? $"next {When(s.NextRunAt)}" : "done")}{(s.Cron is null ? "" : $" (cron {s.Cron})")}" +
            (s.LastStatus is null ? "" : $", last run {When(s.LastRunAt)} {s.LastStatus}{(s.LastError is null ? "" : $": {s.LastError}")}")));
    }

    [Description("Cancels a scheduled task by its id.")]
    public string CancelSchedule([Description("The task's id")] string id) =>
        Guid.TryParse(id, out var g) && Delete(g) ? "Cancelled." : "No such task.";

    string When(DateTimeOffset? t) => t is { } v ? TimeZoneInfo.ConvertTime(v, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    List<Schedule> Read() => File.Exists(file) ? JsonSerializer.Deserialize<List<Schedule>>(File.ReadAllText(file), AIJsonUtilities.DefaultOptions) ?? [] : [];

    void Write(List<Schedule> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, AIJsonUtilities.DefaultOptions));
        File.Move(temp, file, overwrite: true);
    }
}
