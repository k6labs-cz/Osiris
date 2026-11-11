namespace Osiris;

/// <summary>What a guard is asked about.</summary>
public enum GuardStage
{
    /// <summary>The user's message, before the agent acts on it.</summary>
    UserMessage,

    /// <summary>The agent's final answer or a question to the user, before it's shown.</summary>
    AssistantAnswer,

    /// <summary>A tool call the model asked for, before it runs.</summary>
    ToolCall,
}

/// <summary>The thing to judge.</summary>
/// <param name="Stage">What <paramref name="Text"/> is.</param>
/// <param name="Text">The message or answer; for a tool call its arguments as JSON.</param>
/// <param name="ToolName">The tool's name at <see cref="GuardStage.ToolCall"/>.</param>
/// <param name="Arguments">The tool's arguments at <see cref="GuardStage.ToolCall"/>.</param>
public sealed record GuardRequest(GuardStage Stage, string Text, string? ToolName = null, IReadOnlyDictionary<string, object?>? Arguments = null);

/// <summary>A guard's answer.</summary>
/// <param name="Allowed">False stops the message, answer or tool call.</param>
/// <param name="Reason">Why; the model sees it for a refused tool call.</param>
public readonly record struct GuardVerdict(bool Allowed, string? Reason = null)
{
    public static GuardVerdict Allow { get; } = new(true);

    public static GuardVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// A check every user message, final answer and tool call passes before it takes effect. A refused message or
/// answer is replaced by a refusal; a refused tool call isn't run and the model learns why.
/// A guard that throws lets the text through, so a broken guard never takes the agent down: fail closed inside
/// <see cref="CheckAsync"/> yourself where that's the wrong default.
/// </summary>
public interface IGuard
{
    ValueTask<GuardVerdict> CheckAsync(GuardRequest request, CancellationToken cancellationToken = default);

    /// <summary>A section for the system prompt, so the model knows the rule beforehand; empty for none.</summary>
    string GetPrompt() => "";
}
