using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Osiris.Core;
using Osiris.Ui;
using Osiris.Web;

// The Osiris server: every folder under <DataDirectory>/workspaces is a workspace (tenant) with many users,
// signed in with Anubis, where a workspace is a tenant. Configuration: appsettings.json, environment (Osiris__…, Auth__…), command line.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets(); // serves wwwroot from the build output too, not only after publish
var config = builder.Configuration;

var data = Path.GetFullPath(config["Osiris:DataDirectory"] ?? "data");
var root = Directory.CreateDirectory(Path.Combine(data, "workspaces")).FullName;
var auth = config.GetSection("Auth").Get<AuthOptions>() ?? new();
if (auth.Authority.Length == 0)
    throw new InvalidOperationException("Set Auth:Authority (Auth__Authority) to the address of Anubis, and Auth:ClientId / Auth:ClientSecret to Osiris's client there.");

var host = new AgentOptions
{
    Model = (config.GetSection("Osiris:Model").Get<ModelConfig>() ?? new()).Over(ModelConfig.Default) is var model
        ? model with { ApiKey = model.ApiKey ?? AgentOptions.EnvironmentKey() } : null!,
    SecretKey = AgentOptions.LoadSecretKey(config["Osiris:SecretKey"]),
    Local = false,
    PackagesDirectory = Path.Combine(data, "packages"),
    AllowedPackages = config.GetSection("Osiris:AllowedPackages").Get<string[]>() ?? ["k6labs.Osiris.*"],
};

builder.Services.Configure<AuthOptions>(config.GetSection("Auth"));
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<TokenIntrospection>();
builder.Services.AddSingleton<AnubisAccess>();
// The sign-in cookie must survive restarts and be readable by the next pod.
builder.Services.AddDataProtection().SetApplicationName("osiris").PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(data, "keys")));

// Browsers sign in with OpenID Connect into a cookie; API clients send an access token.
const string BrowserOrApi = "browser-or-api";
builder.Services.AddAuthentication(BrowserOrApi)
    .AddPolicyScheme(BrowserOrApi, null, o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? IntrospectionHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, IntrospectionHandler>(IntrospectionHandler.SchemeName, null)
    .AddCookie(o =>
    {
        o.ForwardChallenge = OpenIdConnectDefaults.AuthenticationScheme;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        // Not allowed is 403 for the API and for pages alike; there is no "access denied" page to go to.
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        // The session lives only as long as its access token is active in Anubis: asked again every few minutes,
        // so a role taken away (Anubis revokes the token) or an expired token ends it, and the roles stay current.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            const string CheckedAt = "osiris:checked";
            var now = DateTimeOffset.UtcNow;
            if (ctx.Properties.Items.TryGetValue(CheckedAt, out var at) && DateTimeOffset.TryParse(at, out var last) && now - last < auth.RecheckAfter) return;

            var claims = ctx.Properties.GetTokenValue("access_token") is { } token
                ? await ctx.HttpContext.RequestServices.GetRequiredService<TokenIntrospection>().IntrospectAsync(token, ctx.HttpContext.RequestAborted, fresh: true)
                : null;
            if (claims is null)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            var identity = new ClaimsIdentity(ctx.Principal!.Identity!);
            SetRoles(identity, claims);
            ctx.Properties.Items[CheckedAt] = now.ToString("O");
            ctx.ReplacePrincipal(new ClaimsPrincipal(identity));
            ctx.ShouldRenew = true;
        };
    })
    .AddOpenIdConnect(o =>
    {
        o.Authority = auth.Authority;
        o.RequireHttpsMetadata = auth.RequireHttps;
        o.ClientId = auth.ClientId;
        o.ClientSecret = auth.ClientSecret;
        o.ResponseType = "code";
        o.UsePkce = true;
        o.MapInboundClaims = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.SaveTokens = true; // the access token, for asking Anubis about it again
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        foreach (var scope in new[] { AnubisAccess.RolesClaim }.Concat(auth.Scopes)) o.Scope.Add(scope);
        o.TokenValidationParameters.NameClaimType = "name";
        o.TokenValidationParameters.RoleClaimType = AnubisAccess.RolesClaim;
        // An API client without a token gets 401, not a sign-in page.
        o.Events.OnRedirectToIdentityProvider = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.HandleResponse();
            }
            return Task.CompletedTask;
        };
        // The ID token says who the user is; what they may do comes from the access token, which must be
        // active and issued for Osiris. Without that the user is signed in with no roles: no workspaces.
        o.Events.OnTokenValidated = async ctx =>
        {
            var token = ctx.TokenEndpointResponse?.AccessToken ?? throw new InvalidOperationException("Anubis sent no access token.");
            var claims = await ctx.HttpContext.RequestServices.GetRequiredService<TokenIntrospection>().IntrospectAsync(token, ctx.HttpContext.RequestAborted);
            SetRoles((ClaimsIdentity)ctx.Principal!.Identity!, claims ?? []);
        };
    });

builder.Services.AddOsirisUi(_ => new Workspaces(host, root, many: true, _.GetRequiredService<AnubisAccess>()), signOutPath: "signout");

var app = builder.Build();

app.UseAuthentication();
app.MapGet("/signout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
    .RequireAuthorization();
app.MapOsirisUi();

app.Run();

// The roles of a session are the access token's, never the ID token's or an older check's.
static void SetRoles(ClaimsIdentity identity, IReadOnlyList<Claim> fromAccessToken)
{
    foreach (var old in identity.FindAll(AnubisAccess.RolesClaim).ToList()) identity.RemoveClaim(old);
    identity.AddClaims(fromAccessToken.Where(c => c.Type == AnubisAccess.RolesClaim));
}

/// <summary>For tests hosting the server in-process.</summary>
public partial class Program;
