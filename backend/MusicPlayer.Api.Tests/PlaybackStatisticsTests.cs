using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class PlaybackStatisticsTests
{
    [Fact]
    public async Task GenreSourcesCombineTagsAndPredictionsCaseInsensitively()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
        await using var database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();
        database.SourceRoots.AddRange(
            new MusicSourceRoot { Id = 1, Name = "Music", Path = "music" },
            new MusicSourceRoot { Id = 2, Name = "Disabled", Path = "disabled", IsEnabled = false });
        database.Folders.AddRange(
            new MusicFolder { Id = 1, SourceRootId = 1, Name = "Music", RelativePath = "" },
            new MusicFolder { Id = 2, SourceRootId = 2, Name = "Disabled", RelativePath = "" });
        database.Tracks.AddRange(
            new MusicTrack { Id = 1, SourceRootId = 1, FolderId = 1, RelativePath = "1.mp3", Title = "One", Genre = " Jazz ", PredictedGenre = "rock" },
            new MusicTrack { Id = 2, SourceRootId = 1, FolderId = 1, RelativePath = "2.mp3", Title = "Two", Genre = " ROCK ", PredictedGenre = "electronic" },
            new MusicTrack { Id = 3, SourceRootId = 1, FolderId = 1, RelativePath = "3.mp3", Title = "Missing", Genre = "Missing", IsAvailable = false },
            new MusicTrack { Id = 4, SourceRootId = 2, FolderId = 2, RelativePath = "4.mp3", Title = "Disabled", PredictedGenre = "Disabled" },
            new MusicTrack { Id = 5, SourceRootId = 1, FolderId = 1, RelativePath = "5.mp3", Title = "Empty", Genre = " ", PredictedGenre = " " });
        await database.SaveChangesAsync();

        var genres = await new MusicLibraryBrowseService(database).GetGenreSourcesAsync(CancellationToken.None);

        Assert.Equal(3, genres.Count);
        var jazz = Assert.Single(genres, genre => genre.Name.Equals("jazz", StringComparison.OrdinalIgnoreCase));
        Assert.True(jazz.FromTags);
        Assert.False(jazz.FromAnalysis);
        var electronic = Assert.Single(genres, genre => genre.Name.Equals("electronic", StringComparison.OrdinalIgnoreCase));
        Assert.False(electronic.FromTags);
        Assert.True(electronic.FromAnalysis);
        var rock = Assert.Single(genres, genre => genre.Name.Equals("rock", StringComparison.OrdinalIgnoreCase));
        Assert.True(rock.FromTags);
        Assert.True(rock.FromAnalysis);
    }

    [Fact]
    public async Task BrowseFiltersByGenreAndLatestListeningStateForCurrentUser()
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
        for (var id = 1; id <= 5; id++)
            database.Tracks.Add(new MusicTrack
            {
                Id = id, SourceRootId = 1, FolderId = 1, RelativePath = $"{id}.mp3",
                Title = $"Song {id}", Genre = id == 5 ? "Jazz" : "Rock",
                DurationSeconds = 100, IsAvailable = id != 4,
            });
        database.PlaybackHistory.AddRange(
            Entry(1, 1, "old", 100, 1, completed: true),
            Entry(1, 1, "partial", 20, 2),
            Entry(1, 2, "complete", 100, 3, completed: true),
            Entry(2, 3, "other-user", 100, 4, completed: true),
            Entry(1, 4, "missing", 100, 5, completed: true));
        await database.SaveChangesAsync();
        var library = new MusicLibraryBrowseService(database);

        Assert.Equal(new[] { "Jazz", "Rock" }, await library.GetGenresAsync(CancellationToken.None));
        async Task<int[]> Find(string status, string? genre = null, string? query = null) =>
            (await library.SearchListeningTracksAsync(1, query, genre, status, 1, 50, CancellationToken.None))
                .Items.Select(track => track.Id).ToArray();
        Assert.Equal(new[] { 1 }, await Find("progress"));
        Assert.Equal(new[] { 2 }, await Find("recent"));
        Assert.Equal(new[] { 3, 5 }, await Find("unplayed"));
        Assert.Equal(new[] { 3 }, await Find("unplayed", "rock"));
        Assert.Equal(new[] { 5 }, await Find("all", "JAZZ", "Song 5"));
        var page = await library.SearchListeningTracksAsync(1, null, null, "all", 2, 2, CancellationToken.None);
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }

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
