namespace Osiris.Core;

/// <summary>
/// Starts the workspaces' scheduled tasks when their time comes (checked twice a minute), for as long as the host
/// runs. Several apps may be open over one workspace: only the one holding the workspace's lock runs its tasks.
/// </summary>
public static class AgentScheduler
{
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(Func<IEnumerable<Agent>> agents, Action<Agent, Exception>? failed = null, CancellationToken ct = default)
    {
        var locks = new Dictionary<string, IDisposable>();
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                foreach (var agent in agents())
                {
                    if (!locks.ContainsKey(agent.Workspace.Root))
                    {
                        if (agent.Workspace.TryLock("scheduler") is not { } held) continue;
                        locks[agent.Workspace.Root] = held;
                    }
                    // Not awaited: a long task must not hold the others; a task is taken once, so the next tick can't start it again.
                    _ = RunDueAsync(agent, failed, ct);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var held in locks.Values) held.Dispose();
        }
    }

    static async Task RunDueAsync(Agent agent, Action<Agent, Exception>? failed, CancellationToken ct)
    {
        try
        {
            await agent.RunDueSchedulesAsync(DateTimeOffset.UtcNow, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            failed?.Invoke(agent, e);
        }
    }
}
