using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Osiris.Ui;

namespace Osiris.Web;

/// <summary>Sign-in with Anubis (or another OpenID Connect provider with token introspection).</summary>
public sealed class AuthOptions
{
    /// <summary>The provider's address, e.g. <c>https://id.example.com</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>False only for an Anubis on plain http on the developer's machine.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Osiris's client there; it needs the authorization code flow and the introspection endpoint.</summary>
    public string ClientId { get; set; } = "osiris";
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// The audience an access token must be issued for; Osiris's client id unless set. Anubis issues the token
    /// for the client that asks for the <c>roles</c> scope.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>More scopes to ask for at sign-in, besides <c>openid profile roles</c>.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>The role of Osiris's client in Anubis that may use a workspace and change its settings.</summary>
    public string AdministratorRole { get; set; } = "Administrator";

    /// <summary>The role that may use a workspace.</summary>
    public string UserRole { get; set; } = "User";

    /// <summary>How often a browser session's access token is checked again, so a role taken away in Anubis ends the session.</summary>
    public TimeSpan RecheckAfter { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The server's rule. A workspace is a tenant in Anubis (the workspace's id is the tenant's), and Osiris's client
/// there defines two roles. A user gets into a workspace with the role in that tenant — <c>acme:User</c> or
/// <c>acme:Administrator</c> in the token's <c>roles</c> — or with the role in all tenants (<c>User</c>,
/// <c>Administrator</c>); only administrators change settings. The token itself must be issued for Osiris
/// (its audience), which is checked when the user is authenticated.
/// </summary>
public sealed class AnubisAccess(IOptions<AuthOptions> options) : IWorkspaceAccess
{
    /// <summary>The claim holding one role: <c>tenant:Role</c>, or <c>Role</c> for all tenants.</summary>
    public const string RolesClaim = "roles";

    public bool CanUse(ClaimsPrincipal user, string workspace) => Has(user, workspace, options.Value.UserRole) || CanAdminister(user, workspace);

    public bool CanAdminister(ClaimsPrincipal user, string workspace) => Has(user, workspace, options.Value.AdministratorRole);

    static bool Has(ClaimsPrincipal user, string workspace, string role) => user.HasClaim(RolesClaim, $"{workspace}:{role}") || user.HasClaim(RolesClaim, role);
}

/// <summary>
/// Asks the provider about an access token (RFC 7662). Anubis's access tokens are encrypted for Anubis, so a
/// resource server can't read them itself: it learns the subject and roles here, and that the token is active
/// and issued for Osiris. Anubis revokes a token when a role in it is taken away, so asking again ends the access.
/// </summary>
public sealed class TokenIntrospection(HttpClient http, IOptions<AuthOptions> options, IMemoryCache cache)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    readonly ConfigurationManager<OpenIdConnectConfiguration> metadata = new(
        $"{options.Value.Authority.TrimEnd('/')}/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever(http) { RequireHttps = options.Value.RequireHttps });

    /// <summary>
    /// The token's claims (<c>sub</c>, <c>name</c>, one <see cref="AnubisAccess.RolesClaim"/> per role), or null when it isn't active or isn't for Osiris.
    /// </summary>
    /// <param name="token">The access token.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <param name="fresh">Asks Anubis even when a recent answer is remembered (the browser session's own, already spaced-out check).</param>
    public async Task<IReadOnlyList<Claim>?> IntrospectAsync(string token, CancellationToken ct = default, bool fresh = false)
    {
        var key = "introspection:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (!fresh && cache.TryGetValue(key, out IReadOnlyList<Claim>? known)) return known;

        var endpoint = (await metadata.GetConfigurationAsync(ct)).IntrospectionEndpoint
            ?? throw new InvalidOperationException("The identity provider has no introspection endpoint.");
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["token_type_hint"] = "access_token",
            ["client_id"] = options.Value.ClientId,
            ["client_secret"] = options.Value.ClientSecret,
        }), ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;

        List<Claim>? claims = null;
        var audience = options.Value.Audience is { Length: > 0 } configured ? configured : options.Value.ClientId;
        if (root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True && Strings(root, "aud").Contains(audience))
        {
            claims = [];
            foreach (var name in new[] { "sub", "name", "preferred_username", "email" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) claims.Add(new Claim(name, value.GetString()!));
            claims.AddRange(Strings(root, AnubisAccess.RolesClaim).Select(r => new Claim(AnubisAccess.RolesClaim, r)));
        }
        return cache.Set<IReadOnlyList<Claim>?>(key, claims, CacheFor);
    }

    /// <summary>A claim that's one string or an array of them.</summary>
    static IEnumerable<string> Strings(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ? []
        : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
        : value.ValueKind == JsonValueKind.String ? [value.GetString()!] : [];
}

/// <summary>API clients: <c>Authorization: Bearer &lt;access token&gt;</c>, checked by introspection.</summary>
public sealed class IntrospectionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TokenIntrospection introspection)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Bearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        try
        {
            if (await introspection.IntrospectAsync(header[7..].Trim(), Context.RequestAborted) is not { } claims) return AuthenticateResult.Fail("The token isn't active or isn't issued for Osiris.");
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", AnubisAccess.RolesClaim)), SchemeName));
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException)
        {
            return AuthenticateResult.Fail(e);
        }
    }
}
