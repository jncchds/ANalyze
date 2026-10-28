using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ANalyze.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace ANalyze.Web.Auth;

/// <summary>
/// Optional protection: without ANalyze__Password everything is open (LAN use). With it, the UI signs in
/// with a cookie and scripts can send "Authorization: Bearer &lt;ANalyze__ApiToken&gt;".
/// </summary>
public static class AuthSetup
{
    public const string Scheme = "analyze";
    public const string TokenScheme = "token";
    public const string Policy = "app";

    public static void AddAnalyzeAuth(this IServiceCollection services, AnalyzeOptions options)
    {
        services.AddAuthentication(Scheme)
            .AddPolicyScheme(Scheme, null, o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? TokenScheme : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "analyze_auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                // API clients get status codes, never redirects.
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, TokenHandler>(TokenScheme, null);

        services.AddAuthorization(o => o.AddPolicy(Policy, p =>
        {
            if (string.IsNullOrEmpty(options.Password))
            {
                p.RequireAssertion(_ => true);
            }
            else
            {
                p.RequireAuthenticatedUser();
            }
        }));
    }

    public static bool PasswordMatches(string? expected, string? given)
    {
        if (string.IsNullOrEmpty(expected) || given is null)
        {
            return false;
        }

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(given));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static ClaimsPrincipal Principal(string how) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner"), new Claim("amr", how)], how));
}

public sealed class TokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory logger, UrlEncoder encoder, IOptions<AnalyzeOptions> app)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var expected = app.Value.ApiToken;
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(AuthSetup.PasswordMatches(expected, token)
            ? AuthenticateResult.Success(new AuthenticationTicket(AuthSetup.Principal(AuthSetup.TokenScheme), Scheme.Name))
            : AuthenticateResult.Fail("Invalid token"));
    }
}
