# Writing an extension

An extension is a class library, released as a NuGet package, that gives the Osiris agent more **tools**,
**guards** or **MCP servers**. A workspace adds it by id and version in `.osiris/osiris.json`:

```json
{ "packages": { "Acme.Osiris.Weather": "1.0.0" } }
```

Osiris downloads the package with its dependencies, loads it, and creates every public class in it that
implements one of three interfaces from [`k6labs.Osiris.Abstractions`](../src/Osiris.Abstractions).
[`Osiris.Tools.Database`](../src/Osiris.Tools.Database) is a complete example.

## The project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Acme.Osiris.Weather</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="k6labs.Osiris.Abstractions" Version="0.1.0" />
  </ItemGroup>
</Project>
```

The package's own dependencies are installed with it. What Osiris has already — the abstractions,
`Microsoft.Extensions.AI`, `Microsoft.Data.Sqlite` — is shared with the package, in Osiris's version.

## Tools

```csharp
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Osiris;

public sealed class WeatherTools(OsirisContext context) : IToolProvider
{
    public IEnumerable<AITool> GetTools() => [AIFunctionFactory.Create(Forecast, "weather_forecast")];

    // Optional: a section of the system prompt telling the model when to use the tools.
    public string GetPrompt() => "# WEATHER\nFor questions about the weather use weather_forecast.";

    [Description("The weather forecast for a city.")]
    async Task<string> Forecast([Description("The city's name")] string city, CancellationToken ct) => …;
}
```

- The constructor takes an `OsirisContext` (or nothing). It carries the **workspace** (`context.Workspace`),
  the package's **settings** from `osiris.json` (`context.GetSettings<MySettings>()`) and
  `AllowPrivateUrls` — false on a shared server, where an extension must not reach localhost or private
  networks for a workspace.
- One instance is made per workspace; `GetTools()` is asked on every run.
- Name tools in `snake_case` with a prefix of your own; names are unique across the agent.
- A tool that throws hands its message to the model as the result, so the model can correct itself.
  Return short text or compact JSON: it's paid in tokens.
- Run every path that comes from the model through `context.Workspace.Resolve(path)`: it refuses anything
  outside the workspace or in a hidden folder. Keep your own data in `context.Workspace.DataOf("name")`
  (inside `.osiris/`, out of the agent's reach).

## Guards

A guard checks every user message, final answer and tool call before it takes effect.

```csharp
public sealed class NoDeletes : IGuard
{
    public ValueTask<GuardVerdict> CheckAsync(GuardRequest request, CancellationToken ct = default) =>
        new(request is { Stage: GuardStage.ToolCall, ToolName: "db_delete" }
            ? GuardVerdict.Deny("Deleting rows isn't allowed in this workspace.")
            : GuardVerdict.Allow);
}
```

A refused message or answer is replaced by a refusal; a refused tool call isn't run and the model is told
why. A guard that throws lets the request through (a broken guard must not take the agent down), so catch
and deny inside `CheckAsync` where failing open is the wrong default. `GetPrompt()` can put the rule into
the system prompt, so the model knows it beforehand.

The built-in guard is the workspace's rules in `.osiris/guards/*.md`, judged by the model.

## MCP servers

A package can bring MCP servers (HTTP) next to the ones in `osiris.json`:

```csharp
public sealed class AcmeServers(OsirisContext context) : IMcpServerProvider
{
    public IEnumerable<McpServerDefinition> GetServers() =>
        [new("acme-crm", "https://crm.acme.example/mcp", new Dictionary<string, string> { ["Authorization"] = "Bearer {{secret:ACME_TOKEN}}" })];
}
```

`{{secret:NAME}}` is filled from the workspace's secrets, only for the hosts the secret allows; the model
never sees the value.

## Trying it locally

Pack into a folder and name the folder as a source:

```bash
dotnet pack -o ~/osiris-feed
```

```json
{ "packages": { "Acme.Osiris.Weather": "1.0.0" }, "sources": [ "/home/me/osiris-feed" ] }
```

An unpacked package is cached by id and version (`~/.local/share/osiris/packages`, `%LocalAppData%\osiris\packages`):
raise the version, or delete its folder there, to load a rebuilt one. A package that fails to load is left
out and named to the model and in the settings page, with the reason.

## Releasing

Push the package to nuget.org or to a GitHub Packages feed. Workspaces find it on nuget.org and on k6labs'
feed without more configuration; for another feed they add it to `sources`. A server's operator must allow
the id (`Osiris:AllowedPackages`).
