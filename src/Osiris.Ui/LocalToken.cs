using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Osiris.Ui;

/// <summary>
/// A single-user app has no sign-in, but its UI is a web server on localhost that any local program or web page
/// could call: only who knows the token made at start gets in. It comes once as <c>?token=</c> in the address the
/// app opens (kept in a cookie from then on), or as <c>Authorization: Bearer</c>.
/// </summary>
public sealed class LocalTokenHandler(IOptionsMonitor<LocalTokenOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<LocalTokenOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalToken";
    const string Cookie = "osiris-token";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var query = Request.Query["token"].ToString();
        var given = header.StartsWith("Bearer ") ? header[7..] : query is { Length: > 0 } ? query : Request.Cookies[Cookie];
        if (string.IsNullOrEmpty(given)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Options.Token)))
            return Task.FromResult(AuthenticateResult.Fail("Wrong token."));

        if (query.Length > 0)
            Response.Cookies.Append(Cookie, given, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "local"), new Claim("name", Environment.UserName)], SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Response.WriteAsync("Open Osiris from the app that started it.");
    }
}

public sealed class LocalTokenOptions : AuthenticationSchemeOptions
{
    public string Token { get; set; } = "";
}
