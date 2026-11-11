using Microsoft.Extensions.AI;

namespace Osiris;

/// <summary>
/// Tools for the agent. Osiris creates one instance per workspace and asks for the tools on every run, so they can
/// follow the workspace's state. Build them with <see cref="AIFunctionFactory"/>; a tool that throws hands its
/// message to the model as the result.
/// </summary>
public interface IToolProvider
{
    /// <summary>The tools; names in <c>snake_case</c>, unique across the agent (prefix them, e.g. <c>db_query</c>).</summary>
    IEnumerable<AITool> GetTools();

    /// <summary>A section for the system prompt telling the model when and how to use the tools; empty for none.</summary>
    string GetPrompt() => "";
}
