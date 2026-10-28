using ANalyze.Infrastructure;
using ANalyze.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace ANalyze.Web.Endpoints;

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public sealed record LoginRequest(string Password);

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").AllowAnonymous();

        g.MapGet("/me", (HttpContext http, IOptions<AnalyzeOptions> o) => new
        {
            required = !string.IsNullOrEmpty(o.Value.Password),
            authenticated = string.IsNullOrEmpty(o.Value.Password) || http.User.Identity?.IsAuthenticated == true,
        });

        g.MapPost("/login", async (LoginRequest req, HttpContext http, IOptions<AnalyzeOptions> o) =>
        {
            if (string.IsNullOrEmpty(o.Value.Password))
            {
                return Results.Ok();
            }

            if (!AuthSetup.PasswordMatches(o.Value.Password, req.Password))
            {
                return Results.Problem("Wrong password", statusCode: StatusCodes.Status401Unauthorized);
            }

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.Principal("pwd"),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Ok();
        }).RequireRateLimiting(RateLimitPolicy);

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
    }
}
