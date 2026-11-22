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
