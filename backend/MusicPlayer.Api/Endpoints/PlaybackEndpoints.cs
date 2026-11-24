using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Endpoints;

public static class PlaybackEndpoints
{
    public static void MapPlaybackEndpoints(this WebApplication app)
    {
        var playback = app.MapGroup("/api/playback").RequireAuthorization().WithTags("Playback");
        playback.MapGet("/browse", async (
            ClaimsPrincipal principal, MusicLibraryBrowseService library, CancellationToken cancellationToken,
            string? query = null, string? genre = null, string status = "all", int page = 1, int pageSize = 50) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            if (query?.Length > 256 || genre?.Length > 256 || status is not ("all" or "recent" or "progress" or "unplayed")
                || page < 1 || page > 1000000 || pageSize is < 1 or > 200)
                return Results.BadRequest("Invalid search filters or page.");
            return Results.Ok(await library.SearchListeningTracksAsync(
                userId, query, genre, status, page, pageSize, cancellationToken));
        }).WithName("BrowseListeningTracks")
          .WithSummary("Search available tracks by genre and listening status.")
          .Produces<PagedResponse<TrackResponse>>();
        playback.MapGet("/genres", async (MusicLibraryBrowseService library, CancellationToken cancellationToken) =>
            TypedResults.Ok(await library.GetGenresAsync(cancellationToken)))
            .WithName("ListGenres")
            .WithSummary("Lists tagged and audio-predicted genres in the available library.");
        playback.MapGet("/genres/sources", async (MusicLibraryBrowseService library, CancellationToken cancellationToken) =>
            TypedResults.Ok(await library.GetGenreSourcesAsync(cancellationToken)))
            .WithName("ListGenreSources")
            .WithSummary("Lists available genres with their tag and audio-analysis sources.");
        playback.MapPost("/similar", async (
            SimilarTrackRequest request, MusicLibraryBrowseService library, CancellationToken cancellationToken) =>
        {
            if (request.TrackId <= 0 || request.ExcludedTrackIds is null
                || request.ExcludedTrackIds.Length > 100000 || request.ExcludedTrackIds.Any(id => id <= 0))
                return Results.BadRequest("Invalid track or queue.");
            var track = await library.FindSimilarTrackAsync(request.TrackId, request.ExcludedTrackIds, cancellationToken);
            return track is null ? Results.NoContent() : Results.Ok(track);
        }).AddEndpointFilter<CsrfEndpointFilter>()
          .WithName("FindSimilarTrack")
          .WithSummary("Finds a similar available track outside the supplied queue.")
          .Produces<TrackResponse>()
          .Produces(StatusCodes.Status204NoContent);
        playback.MapGet("/statistics", async (
            ClaimsPrincipal principal, IPlaybackStatisticsService statistics, CancellationToken cancellationToken) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            return Results.Ok(await statistics.GetAsync(userId, cancellationToken));
        }).WithSummary("Get recently played and partially played tracks")
          .Produces<PlaybackStatisticsResponse>();
        playback.MapGet("/history", async (
            ClaimsPrincipal principal, MusicDbContext database, CancellationToken cancellationToken,
            int page = 1, int pageSize = 50) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            if (page < 1 || pageSize is < 1 or > 100 || page > 1000000)
                return Results.BadRequest("Invalid history page.");
            var history = await database.PlaybackHistory.AsNoTracking()
                .Where(item => item.UserId == userId)
                .OrderByDescending(item => item.StartedAtUnixMilliseconds)
                .ThenByDescending(item => item.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(item => new PlaybackHistoryResponse(
                    item.TrackId, item.PositionSeconds, item.StartedAtUnixMilliseconds,
                    item.UpdatedAtUnixMilliseconds))
                .ToListAsync(cancellationToken);
            return Results.Ok(history);
        });
        playback.MapPost("/poll", async (
            PlaybackPollRequest request, ClaimsPrincipal principal, PlaybackCoordinator coordinator,
            MusicDbContext database, MusicLibraryBrowseService library, CancellationToken cancellationToken) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await coordinator.PollAsync(userId, request, database, library, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        }).AddEndpointFilter<CsrfEndpointFilter>();

        playback.MapPost("/command", async (
            PlaybackCommandRequest request, ClaimsPrincipal principal, PlaybackCoordinator coordinator,
            MusicDbContext database, MusicLibraryBrowseService library, CancellationToken cancellationToken) =>
        {
            if (!AuthEndpoints.TryGetUserId(principal, out var userId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await coordinator.CommandAsync(userId, request, database, library, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(exception.Message);
            }
            catch (PlaybackConflictException exception)
            {
                return Results.Conflict(exception.Message);
            }
        }).AddEndpointFilter<CsrfEndpointFilter>();
    }
}
