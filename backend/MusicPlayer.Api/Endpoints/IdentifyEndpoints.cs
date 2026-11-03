using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;
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

        identify.MapPost("/artists/search", (
            ArtistSearchRequest request,
            IEnumerable<IArtistProvider> providers,
            CancellationToken ct) =>
                Run(async () =>
                {
                    var provider = GetArtistProvider(providers, request.Provider);
                    return TypedResults.Ok(await provider.SearchArtistsAsync(request.Artist, ct));
                }))
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("SearchArtistMatches")
            .WithSummary("Searches an artist metadata provider for matching candidates.");

        identify.MapGet("/artists/matches/{provider}/{id}", (
            string provider,
            string id,
            IEnumerable<IArtistProvider> providers,
            IMusicBrainzClient musicBrainz,
            ImageWebpService imageWebp,
            CancellationToken ct) =>
                Run(async () =>
                {
                    var detail = await GetArtistProvider(providers, provider).GetArtistAsync(id, ct);
                    if (detail is null)
                    {
                        return TypedResults.NotFound();
                    }

                    var description = detail.Description;
                    var previewImage = detail.Image;
                    var previewContentType = detail.ImageContentType;
                    var imageProvider = detail.ImageProvider;
                    if (string.IsNullOrWhiteSpace(description)
                        && !provider.Equals("musicbrainz", StringComparison.OrdinalIgnoreCase))
                    {
                        var linked = await musicBrainz.FindArtistAsync(detail.Match.Name, ct);
                        if (linked is not null)
                        {
                            description = linked.Description;
                            if (string.IsNullOrWhiteSpace(detail.ImageUrl))
                            {
                                previewImage ??= linked.Image;
                                previewContentType ??= linked.ImageContentType;
                                imageProvider ??= linked.ImageProvider;
                            }
                        }
                    }

                    var optimizedImage = previewImage is not null && previewContentType is not null
                        ? await imageWebp.ConvertOrOriginalAsync(previewImage, previewContentType, ct)
                        : null;
                    var imageDataUrl = optimizedImage is not null
                        ? $"data:{optimizedImage.ContentType};base64,{Convert.ToBase64String(optimizedImage.Data)}"
                        : null;
                    return TypedResults.Ok(new ArtistMatchDetailResponse(
                        detail.Match,
                        description,
                        detail.ImageUrl,
                        imageDataUrl,
                        imageProvider));
                }))
            .WithName("GetArtistMatch")
            .WithSummary("Gets description and image details for one artist match.");

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

        identify.MapGet("/image", (string url, HttpContext context, SafeImageFetcher fetcher, ImageWebpService imageWebp, CancellationToken ct) =>
            Run(async () =>
            {
                var image = await fetcher.FetchAsync(url, ct);
                var optimizedImage = await imageWebp.ConvertOrOriginalAsync(image.Data, image.ContentType, ct);
                context.Response.Headers.CacheControl = "private, max-age=3600";
                return TypedResults.File(optimizedImage.Data, optimizedImage.ContentType);
            }))
            .WithName("ProxyRemoteImage")
            .WithSummary("Downloads a remote image for preview without exposing internal networks.");

        identify.MapGet("/local-image", (int rootId, string path, HttpContext context, IAlbumIdentificationService service, ImageWebpService imageWebp, CancellationToken ct) =>
            Run(async () =>
            {
                if (await service.ReadLocalImageAsync(rootId, path, ct) is not { } image)
                {
                    return TypedResults.NotFound();
                }

                var optimizedImage = await imageWebp.ConvertOrOriginalAsync(image.Data, image.ContentType, ct);
                context.Response.Headers.CacheControl = "private, max-age=300";
                return (IResult)TypedResults.File(optimizedImage.Data, optimizedImage.ContentType);
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

    private static IArtistProvider GetArtistProvider(IEnumerable<IArtistProvider> providers, string id) =>
        providers.FirstOrDefault(provider => provider.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            && provider.IsConfigured)
        ?? throw new ArgumentException("The selected artist provider is unknown or not configured.");
}
