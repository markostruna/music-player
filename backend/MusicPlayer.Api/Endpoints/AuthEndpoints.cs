using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Authentication");

        auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return TypedResults.Ok(new { token = tokens.RequestToken });
        })
        .AllowAnonymous()
        .WithName("GetCsrfToken")
        .WithSummary("Gets a token required for state-changing requests.");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("SignIn")
            .WithSummary("Signs in an enabled account.");

        auth.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        })
        .RequireAuthorization()
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("SignOut")
        .WithSummary("Signs out the current account.");

        auth.MapPost("/refresh", async (HttpContext context) =>
        {
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                context.User,
                new AuthenticationProperties { IsPersistent = true, AllowRefresh = true });
            return TypedResults.NoContent();
        })
        .RequireAuthorization()
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("RefreshSession")
        .WithSummary("Renews the signed-in session cookie.");

        auth.MapGet("/me", async Task<Results<Ok<UserResponse>, UnauthorizedHttpResult>> (
            ClaimsPrincipal principal,
            IAccountService accounts,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
            {
                return TypedResults.Unauthorized();
            }

            var user = await accounts.GetUserAsync(userId, cancellationToken);
            return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(user);
        })
        .RequireAuthorization()
        .WithName("GetCurrentUser")
        .WithSummary("Gets the signed-in account and its personal theme.");

        auth.MapPut("/theme", SetThemeAsync)
            .RequireAuthorization()
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("SetCurrentUserTheme")
            .WithSummary("Updates the signed-in user's theme preference.");
    }

    private static async Task<Results<Ok<UserResponse>, UnauthorizedHttpResult>> LoginAsync(
        LoginRequest request,
        HttpContext context,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        var user = await accounts.AuthenticateAsync(request.Email, request.Password, cancellationToken);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("security_stamp", user.SecurityStamp),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, AllowRefresh = true });

        return TypedResults.Ok(new UserResponse(user.Id, user.Email, user.Role, user.Theme));
    }

    private static async Task<Results<Ok<UserResponse>, NotFound>> SetThemeAsync(
        SetThemeRequest request,
        ClaimsPrincipal principal,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId) || !await accounts.SetThemeAsync(userId, request.Theme, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var user = await accounts.GetUserAsync(userId, cancellationToken);
        return user is null ? TypedResults.NotFound() : TypedResults.Ok(user);
    }

    internal static bool TryGetUserId(ClaimsPrincipal principal, out int userId) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
