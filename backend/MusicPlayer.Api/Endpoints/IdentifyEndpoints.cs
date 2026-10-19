using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services.Identification;

namespace MusicPlayer.Api.Endpoints;

public static class IdentifyEndpoints
{
    public static void MapIdentifyEndpoints(this WebApplication app)
    {
        var identify = app.MapGroup("/api/admin/identify")
            .WithTags("Album identification")
            .RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString()));

        identify.MapGet("/providers", (IAlbumIdentificationService service) => TypedResults.Ok(service.GetProviders()))
            .WithName("ListIdentifyProviders")
            .WithSummary("Lists album identification providers and whether they are configured.");

        identify.MapPost("/suggestions", (AlbumSuggestionsRequest request, IAlbumIdentificationService service, CancellationToken ct) =>
                Run(async () => TypedResults.Ok(await service.SuggestAsync(request, ct))))
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("SuggestAlbumMatches")
            .WithSummary("Suggests matches using the album's tags and folder names.");

        identify.MapPost("/search", (AlbumSearchRequest request, IAlbumIdentificationService service, CancellationToken ct) =>
                Run(async () => TypedResults.Ok(await service.SearchAsync(request, ct))))
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("SearchAlbumMatches")
            .WithSummary("Searches a provider by artist, album title and release year.");

        identify.MapGet("/matches/{provider}/{**id}", (string provider, string id, IAlbumIdentificationService service, CancellationToken ct) =>
                Run(async () => await service.GetMatchAsync(provider, id, ct) is { } detail
                    ? TypedResults.Ok(detail)
                    : (IResult)TypedResults.NotFound()))
            .WithName("GetAlbumMatch")
            .WithSummary("Gets album details and the track list of a provider match.");

        identify.MapPost("/covers", (AlbumCoversRequest request, IAlbumIdentificationService service, CancellationToken ct) =>
                Run(async () => TypedResults.Ok(await service.GetCoverCandidatesAsync(request, ct))))
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("ListAlbumCoverCandidates")
            .WithSummary("Lists local and online cover candidates for an album.");

        identify.MapGet("/image", (string url, HttpContext context, SafeImageFetcher fetcher, CancellationToken ct) =>
            Run(async () =>
            {
                var image = await fetcher.FetchAsync(url, ct);
                context.Response.Headers.CacheControl = "private, max-age=3600";
                return TypedResults.File(image.Data, image.ContentType);
            }))
            .WithName("ProxyRemoteImage")
            .WithSummary("Downloads a remote image for preview without exposing internal networks.");

        identify.MapGet("/local-image", (int rootId, string path, HttpContext context, IAlbumIdentificationService service, CancellationToken ct) =>
            Run(async () =>
            {
                if (await service.ReadLocalImageAsync(rootId, path, ct) is not { } image)
                {
                    return TypedResults.NotFound();
                }

                context.Response.Headers.CacheControl = "private, max-age=300";
                return (IResult)TypedResults.File(image.Data, image.ContentType);
            }))
            .WithName("GetLocalAlbumImage")
            .WithSummary("Returns an image stored inside a configured source root.");

        identify.MapPost("/move/preview", (AlbumMoveRequest request, IAlbumIdentificationService service, CancellationToken ct) =>
                Run(async () => TypedResults.Ok(await service.PlanMoveAsync(request, ct))))
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("PreviewAlbumMove")
            .WithSummary("Shows where an album would be moved and whether that is possible.");

        identify.MapPost("/move", (AlbumMoveRequest request, IAlbumIdentificationService service, CancellationToken ct) =>
                Run(async () => TypedResults.Ok(await service.MoveAsync(request, ct))))
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("MoveAlbum")
            .WithSummary("Moves an album folder to <artist>/<year> - <album>.");
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (AlbumProviderException exception)
        {
            return TypedResults.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (KeyNotFoundException exception)
        {
            return TypedResults.NotFound(exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return TypedResults.BadRequest(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return TypedResults.Problem(
                "The file system rejected the operation: " + exception.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }
}
