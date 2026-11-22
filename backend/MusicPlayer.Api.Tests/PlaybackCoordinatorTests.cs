using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class PlaybackCoordinatorTests
{
    [Theory]
    [InlineData("radio-track", 1)]
    [InlineData("radio-album", 2)]
    public async Task RadioReplacesQueueAtTheEndAndSurvivesCoordinatorRestart(string mode, int queueSize)
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(session, -1);
        var playing = await fixture.Command(session, initial.Revision, mode);
        Assert.Equal(queueSize, playing.Queue!.Count);
        fixture.Coordinator = new PlaybackCoordinator(fixture.Clock);
        var restored = await fixture.Poll(session, -1);
        for (var i = 0; i < queueSize; i++)
            restored = await fixture.Command(session, restored.Revision, "ended", playId: restored.PlayId);
        Assert.True(restored.IsPlaying);
        Assert.Equal(0, restored.CurrentIndex);
        Assert.Equal(queueSize, restored.Queue!.Count);
        Assert.NotEqual(playing.PlayId, restored.PlayId);

        var regular = await fixture.Command(session, restored.Revision, "play", [1], 0);
        var ended = await fixture.Command(session, regular.Revision, "ended", playId: regular.PlayId);
        Assert.False(ended.IsPlaying);
    }

    [Fact]
    public async Task StartingAtSavedPositionPreservesPreviousTrackHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        (await fixture.Database.Tracks.SingleAsync(track => track.Id == 2)).DurationSeconds = 120;
        await fixture.Database.SaveChangesAsync();
        var session = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(session, -1);
        var playing = await fixture.Command(session, initial.Revision, "play", [1], 0);
        await fixture.Poll(session, playing.Revision, 15, true);

        var resumed = await fixture.Coordinator.CommandAsync(1,
            new PlaybackCommandRequest(session, playing.Revision, "play", [2], 0, 17, null,
                WasPlaying: true, StartPositionSeconds: 45),
            fixture.Database, new MusicLibraryBrowseService(fixture.Database), CancellationToken.None);

        Assert.Equal(45, resumed.PositionSeconds);
        Assert.Equal(17, (await fixture.Database.PlaybackHistory.SingleAsync()).PositionSeconds);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Coordinator.CommandAsync(1,
            new PlaybackCommandRequest(session, resumed.Revision, "play", [2], 0, null, null,
                StartPositionSeconds: 120),
            fixture.Database, new MusicLibraryBrowseService(fixture.Database), CancellationToken.None));
    }

    [Fact]
    public async Task RemoteCommandsAndHistorySurviveCoordinatorRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1, 2], 0);
        var progress = await fixture.Poll(a, playing.Revision, 37.5, true);
        var remote = await fixture.Poll(b, -1);

        Assert.Null(progress.Queue);
        Assert.Equal(a, remote.OwnerSessionId);
        Assert.Equal([1, 2], remote.Queue!.Select(track => track.Id));
        Assert.Equal(37.5, remote.PositionSeconds);
        var paused = await fixture.Command(b, remote.Revision, "toggle");
        Assert.False(paused.IsPlaying);
        await fixture.Poll(a, paused.Revision, 38, false);
        var history = await fixture.Database.PlaybackHistory.SingleAsync();
        Assert.Equal(1, history.TrackId);
        Assert.Equal(38, history.PositionSeconds);

        fixture.Coordinator = new PlaybackCoordinator(fixture.Clock);
        var restored = await fixture.Poll(b, -1);
        Assert.Equal([1, 2], restored.Queue!.Select(track => track.Id));
        Assert.Equal(38, restored.PositionSeconds);
        Assert.False(restored.IsPlaying);
        Assert.Equal(b, restored.OwnerSessionId);
    }

    [Fact]
    public async Task TransferWaitsForOwnerToStopBeforeTargetTakesOver()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1], 0);
        await fixture.Poll(a, playing.Revision, 20, true);
        var remote = await fixture.Poll(b, -1);
        var requested = await fixture.Command(b, remote.Revision, "transfer", target: b);

        Assert.Equal(a, requested.OwnerSessionId);
        Assert.Equal(b, requested.TransferTargetSessionId);
        var instruction = await fixture.Poll(a, playing.Revision, 21, true);
        Assert.Equal(a, instruction.OwnerSessionId);
        var transferred = await fixture.Poll(a, instruction.Revision, 21, false, true);
        Assert.Equal(b, transferred.OwnerSessionId);
        Assert.Null(transferred.TransferTargetSessionId);
        Assert.Equal(21, transferred.PositionSeconds);
        Assert.True(transferred.IsPlaying);
    }

    [Fact]
    public async Task PlayOnThisDeviceStartsAPausedSessionAfterHandoff()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1], 0);
        var paused = await fixture.Command(a, playing.Revision, "toggle");
        var remote = await fixture.Poll(b, -1);

        var requested = await fixture.Command(b, remote.Revision, "transfer", target: b, startPlaying: true);

        Assert.False(paused.IsPlaying);
        Assert.True(requested.IsPlaying);
        Assert.Equal(a, requested.OwnerSessionId);
        var transferred = await fixture.Poll(a, requested.Revision, 7, false, true);
        Assert.True(transferred.IsPlaying);
        Assert.Equal(b, transferred.OwnerSessionId);
    }

    [Fact]
    public async Task ExpiredOwnerIsReplacedAndStaleCommandsAreRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1, 2], 0);
        await fixture.Poll(a, playing.Revision, 12, true);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var remote = await fixture.Poll(b, -1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        var takeover = await fixture.Poll(b, remote.Revision);

        Assert.Equal(b, takeover.OwnerSessionId);
        Assert.Equal(12, takeover.PositionSeconds);
        Assert.True(takeover.IsPlaying);
        await Assert.ThrowsAsync<PlaybackConflictException>(() =>
            fixture.Command(b, remote.Revision, "next"));
        var next = await fixture.Command(b, takeover.Revision, "next");
        Assert.Equal(1, next.CurrentIndex);
    }

    [Fact]
    public async Task RemovedTracksDoNotShiftTheCurrentTrackIncorrectly()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1, 2, 1], 1);
        var removed = await fixture.Database.Tracks.SingleAsync(track => track.Id == 1);
        removed.IsAvailable = false;
        await fixture.Database.SaveChangesAsync();

        var restored = await fixture.Poll(a, playing.Revision);
        Assert.Equal([2], restored.Queue!.Select(track => track.Id));
        Assert.Equal(0, restored.CurrentIndex);
    }

    [Fact]
    public async Task RemovingCurrentTrackKeepsTheRemainingQueueSelectable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1, 2], 0);
        (await fixture.Database.Tracks.SingleAsync(track => track.Id == 1)).IsAvailable = false;
        await fixture.Database.SaveChangesAsync();

        var restored = await fixture.Poll(a, playing.Revision);
        Assert.Equal([2], restored.Queue!.Select(track => track.Id));
        Assert.Equal(0, restored.CurrentIndex);
        Assert.False(restored.IsPlaying);
        var resumed = await fixture.Command(a, restored.Revision, "toggle");
        await fixture.Poll(a, resumed.Revision, 5, true);
        Assert.Equal(2, (await fixture.Database.PlaybackHistory.SingleAsync()).TrackId);
    }

    [Fact]
    public async Task AShortTrackIsRecordedEvenIfItEndsBeforeTheNextPoll()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1, 2], 0);

        await fixture.Command(a, playing.Revision, "ended", position: 0.8, playId: playing.PlayId);

        var entry = await fixture.Database.PlaybackHistory.SingleAsync();
        Assert.Equal(1, entry.TrackId);
        Assert.Equal(0.8, entry.PositionSeconds);
        Assert.NotNull(entry.CompletedAtUnixMilliseconds);
        await fixture.Poll(a, playing.Revision, 0.6, true, playId: playing.PlayId);
        Assert.NotNull(entry.CompletedAtUnixMilliseconds);
    }

    [Fact]
    public async Task LatePlaybackReportIsRecordedForTheTrackActuallyPlayed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1, 2], 0);
        var remote = await fixture.Poll(b, -1);
        await fixture.Command(b, remote.Revision, "next");

        await fixture.Poll(a, playing.Revision, 1.2, true, playId: playing.PlayId);

        var entry = await fixture.Database.PlaybackHistory.SingleAsync();
        Assert.Equal(1, entry.TrackId);
        Assert.Equal(1.2, entry.PositionSeconds);
    }

    [Fact]
    public async Task AnOldEndedEventCannotAdvanceTheNewTrack()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var playing = await fixture.Command(a, initial.Revision, "play", [1, 2, 1], 0);
        var next = await fixture.Command(a, playing.Revision, "next");

        await Assert.ThrowsAsync<PlaybackConflictException>(() =>
            fixture.Command(a, next.Revision, "ended", position: 0.8, playId: playing.PlayId));
        var current = await fixture.Poll(a, next.Revision);
        Assert.Equal(1, current.CurrentIndex);
    }

    [Fact]
    public async Task LargeQueuesKeepEveryEntryAndSelectedIndex()
    {
        await using var fixture = await Fixture.CreateAsync();
        var a = Guid.NewGuid().ToString();
        var initial = await fixture.Poll(a, -1);
        var ids = Enumerable.Repeat(1, 10001).ToArray();

        var playing = await fixture.Command(a, initial.Revision, "play", ids, 10000);

        Assert.Equal(10001, playing.Queue!.Count);
        Assert.Equal(10000, playing.CurrentIndex);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class Fixture(SqliteConnection connection, MusicDbContext database) : IAsyncDisposable
    {
        public MusicDbContext Database { get; } = database;
        public TestClock Clock { get; } = new();
        public PlaybackCoordinator Coordinator { get; set; } = null!;
        private MusicLibraryBrowseService Library { get; } = new(database);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
            var database = new MusicDbContext(options);
            await database.Database.EnsureCreatedAsync();
            database.Users.Add(new MusicUser { Id = 1, Email = "test", NormalizedEmail = "TEST", PasswordHash = "test" });
            database.SourceRoots.Add(new MusicSourceRoot { Id = 1, Name = "Music", Path = "music" });
            database.Folders.Add(new MusicFolder { Id = 1, SourceRootId = 1, Name = "Music", RelativePath = "" });
            database.Tracks.AddRange(
                new MusicTrack { Id = 1, SourceRootId = 1, FolderId = 1, RelativePath = "1.mp3", Title = "One" },
                new MusicTrack { Id = 2, SourceRootId = 1, FolderId = 1, RelativePath = "2.mp3", Title = "Two" });
            await database.SaveChangesAsync();
            var fixture = new Fixture(connection, database);
            fixture.Coordinator = new PlaybackCoordinator(fixture.Clock);
            return fixture;
        }

        public Task<PlaybackResponse> Poll(string session, long revision, double position = 0,
            bool playing = false, bool stopped = false, string playId = "") =>
            Coordinator.PollAsync(1, new PlaybackPollRequest(session, session, revision, position, playing, stopped, playId),
                Database, Library, CancellationToken.None);

        public Task<PlaybackResponse> Command(string session, long revision, string action,
            int[]? ids = null, int? index = null, string? target = null, double? position = null,
            string? playId = null, bool startPlaying = false) =>
            Coordinator.CommandAsync(1, new PlaybackCommandRequest(session, revision, action, ids, index, position, target,
                    StartPlaying: startPlaying, ExpectedPlayId: playId),
                Database, Library, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
