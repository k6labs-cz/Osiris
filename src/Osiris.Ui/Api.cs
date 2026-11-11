using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Osiris.Core;

namespace Osiris.Ui;

public sealed record RunRequest(string Message);

/// <summary>A secret to set: <c>Value</c> null keeps the stored one (only hosts or description change).</summary>
public sealed record SecretRequest(string? Value, string[] AllowedHosts, string? Description);

/// <summary>The HTTP API for clients other than the web UI (which calls the agent directly).</summary>
public static class Api
{
    public static void MapOsirisApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/workspaces").RequireAuthorization();

        api.MapGet("/", (Workspaces workspaces, ClaimsPrincipal user) => workspaces.List(user));

        var workspace = api.MapGroup("/{id}");
        workspace.MapGet("/threads", (string id, Workspaces workspaces, ClaimsPrincipal user) =>
            workspaces.Get(id, user) is { } agent ? Results.Ok(agent.Threads.List()) : Results.NotFound());

        workspace.MapGet("/threads/{thread:guid}", (string id, Guid thread, Workspaces workspaces, ClaimsPrincipal user) =>
            workspaces.Get(id, user) is { } agent
                ? Results.Json(agent.Threads.Load(thread), AIJsonUtilities.DefaultOptions)
                : Results.NotFound());

        workspace.MapDelete("/threads/{thread:guid}", (string id, Guid thread, Workspaces workspaces, ClaimsPrincipal user) =>
        {
            if (workspaces.Get(id, user) is not { } agent) return Results.NotFound();
            agent.Threads.Delete(thread);
            return Results.NoContent();
        });

        // The client sends only the new message; the history is on the agent's side.
        workspace.MapPost("/threads/{thread:guid}/runs", (string id, Guid thread, RunRequest body, Workspaces workspaces, ClaimsPrincipal user, CancellationToken ct) =>
            workspaces.Get(id, user) is not { } agent ? Results.NotFound()
            : string.IsNullOrWhiteSpace(body.Message) ? Results.BadRequest("Empty message.")
            : TypedResults.ServerSentEvents(agent.RunAsync(thread, body.Message, ct)));

        // Secrets are an administrator's: names, hosts and descriptions go out, values never.
        workspace.MapGet("/secrets", (string id, Workspaces workspaces, ClaimsPrincipal user) =>
            workspaces.Administer(id, user) is { } agent ? Results.Ok(agent.Secrets.List()) : Denied(id, workspaces, user));

        workspace.MapPut("/secrets/{name}", (string id, string name, SecretRequest body, Workspaces workspaces, ClaimsPrincipal user) =>
        {
            if (workspaces.Administer(id, user) is not { } agent) return Denied(id, workspaces, user);
            try
            {
                agent.Secrets.Put(name, body.Value, body.AllowedHosts ?? [], body.Description ?? "");
                return Results.NoContent();
            }
            catch (SecretException e)
            {
                return Results.BadRequest(e.Message);
            }
        });

        workspace.MapDelete("/secrets/{name}", (string id, string name, Workspaces workspaces, ClaimsPrincipal user) =>
            workspaces.Administer(id, user) is not { } agent ? Denied(id, workspaces, user)
            : agent.Secrets.Delete(name) ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>A workspace the user can't see doesn't exist for them; one they can only use is forbidden to administer.</summary>
    static IResult Denied(string id, Workspaces workspaces, ClaimsPrincipal user) => workspaces.Get(id, user) is null ? Results.NotFound() : Results.Forbid();
}
