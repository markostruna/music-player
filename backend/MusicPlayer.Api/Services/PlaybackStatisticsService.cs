using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public interface IPlaybackStatisticsService
{
    Task<PlaybackStatisticsResponse> GetAsync(int userId, CancellationToken cancellationToken);
}

public sealed class PlaybackStatisticsService(MusicDbContext database, MusicLibraryBrowseService library)
    : IPlaybackStatisticsService
{
    public async Task<PlaybackStatisticsResponse> GetAsync(int userId, CancellationToken cancellationToken)
    {
        var history = database.PlaybackHistory.AsNoTracking().Where(item => item.UserId == userId);
        var latest =
            from item in history
            join track in database.Tracks.AsNoTracking() on item.TrackId equals track.Id
            where track.IsAvailable && track.SourceRoot != null && track.SourceRoot.IsEnabled
                && !history.Any(other => other.TrackId == item.TrackId
                    && (other.StartedAtUnixMilliseconds > item.StartedAtUnixMilliseconds
                        || (other.StartedAtUnixMilliseconds == item.StartedAtUnixMilliseconds && other.Id > item.Id)))
            select new
            {
                item.TrackId,
                item.PositionSeconds,
                item.StartedAtUnixMilliseconds,
                item.CompletedAtUnixMilliseconds,
                item.Id,
                track.DurationSeconds,
                PlayCount = history.Count(other => other.TrackId == item.TrackId),
            };

        var recent = await latest.Where(item => item.CompletedAtUnixMilliseconds != null)
            .OrderByDescending(item => item.CompletedAtUnixMilliseconds)
            .ThenByDescending(item => item.Id).Take(10).ToListAsync(cancellationToken);
        var partial = await latest.Where(item => item.CompletedAtUnixMilliseconds == null && item.PositionSeconds > 0
                && item.DurationSeconds > 0 && item.PositionSeconds < item.DurationSeconds)
            .OrderByDescending(item => item.StartedAtUnixMilliseconds)
            .ThenByDescending(item => item.Id).Take(10).ToListAsync(cancellationToken);
        var ids = recent.Concat(partial).Select(item => item.TrackId).Distinct().ToArray();
        var tracks = (await library.GetTracksByIdsAsync(ids, cancellationToken)).ToDictionary(track => track.Id);

        return new PlaybackStatisticsResponse(
            recent.Where(item => tracks.ContainsKey(item.TrackId))
                .Select(item => new PlayedTrackResponse(tracks[item.TrackId], item.PlayCount, item.PositionSeconds,
                    DateTimeOffset.FromUnixTimeMilliseconds(item.CompletedAtUnixMilliseconds!.Value))).ToArray(),
            partial.Where(item => tracks.ContainsKey(item.TrackId))
                .Select(item => new PlayedTrackResponse(tracks[item.TrackId], item.PlayCount, item.PositionSeconds,
                    DateTimeOffset.FromUnixTimeMilliseconds(item.StartedAtUnixMilliseconds))).ToArray());
    }
}
