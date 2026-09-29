using Microsoft.AspNetCore.Http.HttpResults;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var users = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString()));

        users.MapGet("/", async (IAccountService accounts, CancellationToken cancellationToken) =>
                TypedResults.Ok(await accounts.GetUsersAsync(cancellationToken)))
            .WithName("ListUsers")
            .WithSummary("Lists user accounts for an administrator.");

        users.MapPost("/", async Task<Results<Created<ManagedUserResponse>, Conflict<string>>> (
            CreateUserRequest request,
            IAccountService accounts,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var user = await accounts.CreateUserAsync(request, cancellationToken);
                return TypedResults.Created($"/api/users/{user.Id}", user);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("CreateUser")
        .WithSummary("Creates a guest or administrator account.");

        users.MapPut("/{userId:int}/role", async Task<Results<NoContent, NotFound, Conflict<string>>> (
            int userId,
            SetUserRoleRequest request,
            IAccountService accounts,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return await accounts.SetRoleAsync(userId, request.Role, cancellationToken)
                    ? TypedResults.NoContent()
                    : TypedResults.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("SetUserRole")
        .WithSummary("Changes a user's role without allowing the last admin to be demoted.");

        users.MapPut("/{userId:int}/enabled", async Task<Results<NoContent, NotFound, Conflict<string>>> (
            int userId,
            SetUserEnabledRequest request,
            IAccountService accounts,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return await accounts.SetEnabledAsync(userId, request.IsEnabled, cancellationToken)
                    ? TypedResults.NoContent()
                    : TypedResults.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("SetUserEnabled")
        .WithSummary("Enables or disables an account.");

        users.MapPut("/{userId:int}/password", async Task<Results<NoContent, NotFound>> (
            int userId,
            ResetPasswordRequest request,
            IAccountService accounts,
            CancellationToken cancellationToken) =>
                await accounts.ResetPasswordAsync(userId, request.NewPassword, cancellationToken)
                    ? TypedResults.NoContent()
                    : TypedResults.NotFound())
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("ResetUserPassword")
            .WithSummary("Sets a replacement password for an account.");
    }
}
