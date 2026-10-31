using Microsoft.AspNetCore.Http.HttpResults;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Endpoints;

public static class ProviderSettingsEndpoints
{
    public static void MapProviderSettingsEndpoints(this WebApplication app)
    {
        var providers = app.MapGroup("/api/admin/providers")
            .WithTags("Provider settings")
            .RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString()));

        providers.MapGet("/", async (IProviderSettingsService settings, CancellationToken cancellationToken) =>
                TypedResults.Ok(await settings.GetAsync(cancellationToken)))
            .WithName("GetProviderSettings")
            .WithSummary("Lists provider settings without returning stored credentials.");

        providers.MapPut("/{providerId}", async Task<Results<Ok<IReadOnlyList<ProviderSettingsItem>>, BadRequest<string>, NotFound<string>>> (
            string providerId,
            UpdateProviderSettingsRequest request,
            IProviderSettingsService settings,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await settings.UpdateAsync(providerId, request.Credentials, cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return TypedResults.NotFound(exception.Message);
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("UpdateProviderSettings")
        .WithSummary("Updates shared provider credentials. Empty values clear a credential.");
    }
}
