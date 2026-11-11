using System.Text.Json.Serialization;

namespace Osiris.Core;

/// <summary>What a run streams to clients, as in the original's <c>RunEvent</c> (JSON with a <c>type</c> field).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextDelta), "text-delta")]
[JsonDerivedType(typeof(ToolCall), "tool-call")]
[JsonDerivedType(typeof(ToolResult), "tool-result")]
[JsonDerivedType(typeof(Done), "done")]
[JsonDerivedType(typeof(RunError), "error")]
public abstract record RunEvent;

public sealed record TextDelta(string Delta) : RunEvent;
public sealed record ToolCall(string ToolCallId, string ToolName, IDictionary<string, object?>? Args) : RunEvent;
public sealed record ToolResult(string ToolCallId, string Result) : RunEvent;
public sealed record Done : RunEvent;
public sealed record RunError(string Message) : RunEvent;
