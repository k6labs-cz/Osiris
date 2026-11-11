using System.Reflection;
using k6labs.Hathor.Lucide;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Osiris.Core;
using Osiris.Ui.Components;

namespace Osiris.Ui;

/// <summary>What the page needs to know about the app hosting it.</summary>
/// <param name="StylesBundle">The host's CSS bundle (<c>&lt;assembly&gt;.styles.css</c>), which carries Hathor's and the UI's styles.</param>
/// <param name="SignOutPath">Where "Sign out" leads; null in a single-user app.</param>
public sealed record UiOptions(string StylesBundle, string? SignOutPath = null);

/// <summary>The web UI and API over a set of workspaces: the same for the server and the desktop app, which differ only in sign-in.</summary>
public static class OsirisUi
{
    /// <summary>Registers the UI over <paramref name="workspaces"/>. The host adds its own authentication.</summary>
    public static IServiceCollection AddOsirisUi(this IServiceCollection services, Workspaces workspaces, string? signOutPath = null) =>
        services.AddOsirisUi(_ => workspaces, signOutPath);

    /// <inheritdoc cref="AddOsirisUi(IServiceCollection, Workspaces, string?)"/>
    public static IServiceCollection AddOsirisUi(this IServiceCollection services, Func<IServiceProvider, Workspaces> workspaces, string? signOutPath = null)
    {
        services.AddSingleton(workspaces);
        services.AddSingleton(new UiOptions($"{Assembly.GetEntryAssembly()!.GetName().Name}.styles.css", signOutPath));
        services.AddHostedService<Scheduler>();
        services.AddHathorLucide();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.AddAuthorization();
        return services;
    }

    /// <summary>Maps the UI, the API and <c>/health</c>; call after <c>UseAuthentication</c>.</summary>
    public static WebApplication MapOsirisUi(this WebApplication app)
    {
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapGet("/health", () => "ok");
        app.MapOsirisApi();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
        return app;
    }

    sealed class Scheduler(Workspaces workspaces, ILogger<Scheduler> log) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken ct) => AgentScheduler.RunAsync(workspaces.All,
            (agent, e) => log.LogError(e, "Scheduled tasks of {Workspace} failed", agent.Workspace.Name), ct);
    }
}
