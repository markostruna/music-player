using Microsoft.AspNetCore.Http.HttpResults;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Endpoints;

public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var library = app.MapGroup("/api/library").WithTags("Library").RequireAuthorization();
        library.MapGet("/tracks", async (IMusicLibraryService music, CancellationToken cancellationToken) =>
                TypedResults.Ok(await music.GetTracksAsync(cancellationToken)))
            .WithName("ListTracks")
            .WithSummary("Lists playable tracks in configured music sources.");
        library.MapGet("/albums", async (IMusicLibraryService music, CancellationToken cancellationToken) =>
                TypedResults.Ok(await music.GetAlbumsAsync(cancellationToken)))
            .WithName("ListAlbums")
            .WithSummary("Groups playable tracks into albums.");
        library.MapGet("/tracks/{trackId:int}/stream", async Task<Results<PhysicalFileHttpResult, NotFound>> (
            int trackId,
            IMusicLibraryService music,
            CancellationToken cancellationToken) =>
        {
            var media = await music.ResolveStreamAsync(trackId, cancellationToken);
            return media is null
                ? TypedResults.NotFound()
                : TypedResults.PhysicalFile(media.FullPath, media.ContentType, enableRangeProcessing: true);
        })
        .WithName("StreamTrack")
        .WithSummary("Streams a catalogued track with HTTP range support.");
        library.MapGet("/tracks/{trackId:int}/cover", async Task<Results<FileContentHttpResult, NotFound>> (
            int trackId,
            IMusicLibraryService music,
            CancellationToken cancellationToken) =>
        {
            var image = await music.ResolveCoverAsync(trackId, cancellationToken);
            return image is null ? TypedResults.NotFound() : TypedResults.File(image.Data, image.ContentType);
        })
        .WithName("GetTrackCover")
        .WithSummary("Returns the embedded cover art for a track.");

        var roots = app.MapGroup("/api/admin/roots")
            .WithTags("Source roots")
            .RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString()));
        roots.MapGet("/", async (IMusicLibraryService music, CancellationToken cancellationToken) =>
                TypedResults.Ok(await music.GetRootsAsync(cancellationToken)))
            .WithName("ListSourceRoots")
            .WithSummary("Lists configured source roots for an administrator.");
        roots.MapGet("/folders", async (IMusicLibraryService music, CancellationToken cancellationToken) =>
                TypedResults.Ok(await music.GetFoldersAsync(cancellationToken)))
            .WithName("ListMusicFolders")
            .WithSummary("Lists browsable folders within configured roots.");
        roots.MapPost("/", async Task<Results<Created<SourceRootResponse>, BadRequest<string>, Conflict<string>>> (
            CreateSourceRootRequest request,
            IMusicLibraryService music,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var root = await music.AddRootAsync(request, cancellationToken);
                return TypedResults.Created($"/api/admin/roots/{root.Id}", root);
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("AddSourceRoot")
        .WithSummary("Adds a music folder that the scanner is allowed to access.");
        roots.MapDelete("/{rootId:int}", async Task<Results<NoContent, NotFound, Conflict<string>>> (
            int rootId,
            IMusicLibraryService music,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return await music.RemoveRootAsync(rootId, cancellationToken)
                    ? TypedResults.NoContent()
                    : TypedResults.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("RemoveSourceRoot")
        .WithSummary("Removes a source root and its catalog entries without deleting music files.");
        roots.MapPost("/{rootId:int}/scan", async Task<Results<Ok<ScanResponse>, NotFound, Conflict<string>>> (
            int rootId,
            IMusicLibraryService music,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await music.ScanAsync(rootId, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return TypedResults.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Conflict(exception.Message);
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("ScanSourceRoot")
        .WithSummary("Scans a configured source for WAV, MP3, and FLAC files.");

        var adminTracks = app.MapGroup("/api/admin/tracks")
            .WithTags("Track management")
            .RequireAuthorization(policy => policy.RequireRole(UserRole.Admin.ToString()));
        adminTracks.MapPatch("/{trackId:int}/metadata", async (int trackId, MusicMetadataPatch changes, IMusicLibraryService music, CancellationToken cancellationToken) =>
                TypedResults.Ok(await music.UpdateMetadataAsync([trackId], changes, cancellationToken)))
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("UpdateTrackMetadata")
            .WithSummary("Updates common embedded tags on one track.");
        adminTracks.MapPost("/metadata", async (BatchMetadataRequest request, IMusicLibraryService music, CancellationToken cancellationToken) =>
                TypedResults.Ok(await music.UpdateMetadataAsync(request.TrackIds, request.Changes, cancellationToken)))
            .AddEndpointFilter<CsrfEndpointFilter>()
            .WithName("BatchUpdateTrackMetadata")
            .WithSummary("Updates common embedded tags on selected tracks.");
        adminTracks.MapPost("/cover", async Task<Results<Ok<BatchMutationResponse>, BadRequest<string>>> (
            BatchCoverRequest request,
            IMusicLibraryService music,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await music.UpdateCoverAsync(request.TrackIds, request.Image, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return TypedResults.BadRequest(exception.Message);
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("BatchUpdateTrackCover")
        .WithSummary("Replaces embedded front cover art for selected tracks.");
        adminTracks.MapPost("/move", async Task<Results<Ok<BatchMutationResponse>, NotFound>> (
            MoveTracksRequest request,
            IMusicLibraryService music,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await music.MoveTracksAsync(request.TrackIds, request.DestinationFolderId, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return TypedResults.NotFound();
            }
        })
        .AddEndpointFilter<CsrfEndpointFilter>()
        .WithName("MoveTracks")
        .WithSummary("Moves selected tracks to a folder in a configured source root.");
    }
}
