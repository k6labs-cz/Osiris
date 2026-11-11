using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Osiris.Core;
using Osiris.Ui;
using Photino.NET;

namespace Osiris.Desktop;

/// <summary>
/// Osiris for one user: the web UI of the chosen folder's agent, served on localhost to a window of its own.
/// <c>osiris-desktop [folder]</c>; without an argument it asks for a folder. <c>--no-window</c> prints the address
/// instead, for a browser (and for systems without a web view).
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var folder = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (args.Contains("--no-window"))
        {
            using var app = Start(folder ?? ".", args);
            Console.WriteLine($"Osiris: {Address(app)}");
            app.WaitForShutdown();
            return 0;
        }

        WebApplication? started = null;
        // LogVerbosity 0: Photino would print every URL, the token with it.
        var window = new PhotinoWindow { Title = "Osiris", Width = 1200, Height = 800, Centered = true, LogVerbosity = 0 };
        window.LoadRawString(Page("Starting Osiris…"));
        window.RegisterWindowCreatedHandler(async (_, _) =>
        {
            try
            {
                folder ??= (await window.ShowOpenFolderAsync("Choose a folder for Osiris to work in")).FirstOrDefault();
                if (folder is null) { window.Close(); return; }
                started = Start(folder, args);
                window.Title = $"Osiris · {Path.GetFileName(Path.GetFullPath(folder))}";
                window.Load(Address(started));
            }
            catch (Exception e)
            {
                window.LoadRawString(Page(e.Message));
            }
        });
        window.WaitForClose();
        started?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        return 0;
    }

    /// <summary>The UI over the folder's workspace on a free localhost port, open only to who has the token made here.</summary>
    static WebApplication Start(string folder, string[] args)
    {
        var path = Path.GetFullPath(folder);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"No folder {path}.");
        // The user chose the folder, so it becomes a workspace if it isn't inside one yet.
        var workspace = Workspace.Find(path) ?? new Workspace(path).Initialize();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [.. args.Where(a => a.StartsWith("--") && a.Contains('='))], ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseStaticWebAssets(); // serves wwwroot from the build output too, not only after publish
        builder.WebHost.UseUrls($"http://127.0.0.1:{builder.Configuration["port"] ?? "0"}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        builder.Services.AddSingleton(new StartToken(token));
        builder.Services.AddAuthentication(LocalTokenHandler.SchemeName)
            .AddScheme<LocalTokenOptions, LocalTokenHandler>(LocalTokenHandler.SchemeName, o => o.Token = token);
        builder.Services.AddOsirisUi(new Workspaces(AgentOptions.ForUser(), workspace.Root, many: false, new OwnerAccess()));

        var app = builder.Build();
        app.UseAuthentication();
        app.MapOsirisUi();
        app.Start();
        return app;
    }

    static string Address(WebApplication app) =>
        $"{app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()}/?token={app.Services.GetRequiredService<StartToken>().Value}";

    sealed record StartToken(string Value);

    static string Page(string text) =>
        $"""<body style="font:16px system-ui,sans-serif;background:#0f131b;color:#e6edf3;display:grid;place-items:center;height:90vh">{WebUtility.HtmlEncode(text)}</body>""";
}
