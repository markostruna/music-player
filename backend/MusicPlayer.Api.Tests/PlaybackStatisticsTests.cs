using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class PlaybackStatisticsTests
{
    [Fact]
    public async Task StatisticsUseLatestPlayPerTrackAndExcludeUnavailableTracks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
        await using var database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();
        database.Users.AddRange(
            new MusicUser { Id = 1, Email = "one", NormalizedEmail = "ONE", PasswordHash = "test" },
            new MusicUser { Id = 2, Email = "two", NormalizedEmail = "TWO", PasswordHash = "test" });
        database.SourceRoots.Add(new MusicSourceRoot { Id = 1, Name = "Music", Path = "music" });
        database.Folders.Add(new MusicFolder { Id = 1, SourceRootId = 1, Name = "Music", RelativePath = "" });
        database.Tracks.AddRange(
            new MusicTrack { Id = 1, SourceRootId = 1, FolderId = 1, RelativePath = "1.mp3", Title = "One", DurationSeconds = 100 },
            new MusicTrack { Id = 2, SourceRootId = 1, FolderId = 1, RelativePath = "2.mp3", Title = "Two", DurationSeconds = 100 },
            new MusicTrack { Id = 3, SourceRootId = 1, FolderId = 1, RelativePath = "3.mp3", Title = "Missing", DurationSeconds = 100, IsAvailable = false });
        database.PlaybackHistory.AddRange(
            Entry(1, 1, "old", 20, 10),
            Entry(1, 1, "new", 99, 20, completed: true),
            Entry(1, 2, "partial", 40, 30),
            Entry(1, 3, "missing", 30, 40),
            Entry(2, 2, "other", 80, 50));
        await database.SaveChangesAsync();

        var result = await new PlaybackStatisticsService(database, new MusicLibraryBrowseService(database))
            .GetAsync(1, CancellationToken.None);

        Assert.Equal([1], result.RecentlyPlayed.Select(item => item.Track.Id));
        Assert.Equal(2, result.RecentlyPlayed[0].PlayCount);
        var partial = Assert.Single(result.ContinueListening);
        Assert.Equal(2, partial.Track.Id);
        Assert.Equal(40, partial.PositionSeconds);
        Assert.Equal(1, partial.PlayCount);
    }

    [Fact]
    public async Task RecentlyPlayedContainsOnlyTheTenMostRecentlyCompletedLatestPlays()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
        await using var database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();
        database.Users.Add(new MusicUser { Id = 1, Email = "one", NormalizedEmail = "ONE", PasswordHash = "test" });
        database.SourceRoots.Add(new MusicSourceRoot { Id = 1, Name = "Music", Path = "music" });
        database.Folders.Add(new MusicFolder { Id = 1, SourceRootId = 1, Name = "Music", RelativePath = "" });
        for (var id = 1; id <= 12; id++)
        {
            database.Tracks.Add(new MusicTrack
            {
                Id = id, SourceRootId = 1, FolderId = 1, RelativePath = $"{id}.mp3",
                Title = $"Track {id}", DurationSeconds = 100,
            });
            database.PlaybackHistory.Add(Entry(1, id, $"completed-{id}", 99, id, completed: true));
        }
        database.PlaybackHistory.Local.Single(item => item.TrackId == 1).CompletedAtUnixMilliseconds = 1000;
        database.PlaybackHistory.Add(Entry(1, 12, "partial", 40, 100));
        await database.SaveChangesAsync();

        var result = await new PlaybackStatisticsService(database, new MusicLibraryBrowseService(database))
            .GetAsync(1, CancellationToken.None);

        Assert.Equal(new[] { 1 }.Concat(Enumerable.Range(3, 9).Reverse()),
            result.RecentlyPlayed.Select(item => item.Track.Id));
        Assert.Equal(12, Assert.Single(result.ContinueListening).Track.Id);
    }

    [Fact]
    public async Task ContinueListeningContainsTheTenMostRecentlyPlayedPartialTracks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
        await using var database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();
        database.Users.Add(new MusicUser { Id = 1, Email = "one", NormalizedEmail = "ONE", PasswordHash = "test" });
        database.SourceRoots.Add(new MusicSourceRoot { Id = 1, Name = "Music", Path = "music" });
        database.Folders.Add(new MusicFolder { Id = 1, SourceRootId = 1, Name = "Music", RelativePath = "" });
        for (var id = 1; id <= 11; id++)
        {
            database.Tracks.Add(new MusicTrack
            {
                Id = id, SourceRootId = 1, FolderId = 1, RelativePath = $"{id}.mp3",
                Title = $"Track {id}", DurationSeconds = 100,
            });
            database.PlaybackHistory.Add(Entry(1, id, $"partial-{id}", 40, id));
        }
        await database.SaveChangesAsync();

        var result = await new PlaybackStatisticsService(database, new MusicLibraryBrowseService(database))
            .GetAsync(1, CancellationToken.None);

        Assert.Equal(Enumerable.Range(2, 10).Reverse(), result.ContinueListening.Select(item => item.Track.Id));
    }

    private static PlaybackHistory Entry(int userId, int trackId, string playId, double position, long time,
        bool completed = false) =>
        new() { UserId = userId, TrackId = trackId, PlayId = playId, PositionSeconds = position,
            StartedAtUnixMilliseconds = time, UpdatedAtUnixMilliseconds = time,
            CompletedAtUnixMilliseconds = completed ? time : null };
}
