using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class MusicLibraryMoveTests : IAsyncLifetime
{
    private readonly byte[] audioBytes = [1, 3, 5, 7, 9, 11];
    private SqliteConnection connection = null!;
    private MusicDbContext database = null!;
    private MusicLibraryService library = null!;
    private string testDirectory = null!;
    private string sourceDirectory = null!;
    private string destinationDirectory = null!;
    private int trackId;
    private int destinationFolderId;
    private int sourceRootId;
    private int destinationRootId;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(connection)
            .Options;
        database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();

        testDirectory = Path.Combine(Path.GetTempPath(), $"afterhours-move-{Guid.NewGuid():N}");
        sourceDirectory = Path.Combine(testDirectory, "source");
        destinationDirectory = Path.Combine(testDirectory, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var sourceRoot = new MusicSourceRoot { Name = "Source", Path = sourceDirectory };
        var destinationRoot = new MusicSourceRoot { Name = "Destination", Path = destinationDirectory };
        database.SourceRoots.AddRange(sourceRoot, destinationRoot);
        await database.SaveChangesAsync();
        sourceRootId = sourceRoot.Id;
        destinationRootId = destinationRoot.Id;

        var sourceFolder = new MusicFolder { SourceRootId = sourceRoot.Id, Name = "Source", RelativePath = string.Empty };
        var destinationFolder = new MusicFolder { SourceRootId = destinationRoot.Id, Name = "Destination", RelativePath = string.Empty };
        database.Folders.AddRange(sourceFolder, destinationFolder);
        await database.SaveChangesAsync();
        destinationFolderId = destinationFolder.Id;

        var sourcePath = Path.Combine(sourceDirectory, "track.wav");
        await System.IO.File.WriteAllBytesAsync(sourcePath, audioBytes);
        var track = new MusicTrack
        {
            SourceRootId = sourceRoot.Id,
            FolderId = sourceFolder.Id,
            RelativePath = "track.wav",
            Title = "Track",
            FileSize = audioBytes.Length,
            IsAvailable = true,
        };
        database.Tracks.Add(track);
        await database.SaveChangesAsync();
        trackId = track.Id;
        library = new MusicLibraryService(database, new TestWebHostEnvironment());
    }

    public async Task DisposeAsync()
    {
        await database.DisposeAsync();
        await connection.DisposeAsync();
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MoveTracksUpdatesCatalogAndRemovesSourceRecoveryCopy()
    {
        var response = await library.MoveTracksAsync([trackId], destinationFolderId, CancellationToken.None);

        Assert.True(response.Results.Single().Succeeded);
        Assert.False(System.IO.File.Exists(Path.Combine(sourceDirectory, "track.wav")));
        Assert.Equal(audioBytes, await System.IO.File.ReadAllBytesAsync(Path.Combine(destinationDirectory, "track.wav")));
        Assert.Empty(Directory.EnumerateFiles(sourceDirectory));

        var track = await database.Tracks.SingleAsync();
        Assert.Equal(destinationRootId, track.SourceRootId);
        Assert.Equal("track.wav", track.RelativePath);
    }

    [Fact]
    public async Task MoveTracksRestoresSourceWhenCatalogUpdateConflicts()
    {
        database.Tracks.Add(new MusicTrack
        {
            SourceRootId = destinationRootId,
            FolderId = destinationFolderId,
            RelativePath = "track.wav",
            Title = "Stale destination entry",
            IsAvailable = false,
        });
        await database.SaveChangesAsync();

        var response = await library.MoveTracksAsync([trackId], destinationFolderId, CancellationToken.None);

        Assert.False(response.Results.Single(result => result.TrackId == trackId).Succeeded);
        Assert.Equal(audioBytes, await System.IO.File.ReadAllBytesAsync(Path.Combine(sourceDirectory, "track.wav")));
        Assert.False(System.IO.File.Exists(Path.Combine(destinationDirectory, "track.wav")));
        Assert.DoesNotContain(Directory.EnumerateFiles(sourceDirectory), path => path.Contains(".moving-", StringComparison.Ordinal));

        var track = await database.Tracks.SingleAsync(item => item.Id == trackId);
        Assert.Equal(sourceRootId, track.SourceRootId);
        Assert.Equal("track.wav", track.RelativePath);
    }

    [Fact]
    public async Task MoveTracksPreservesAnExistingDestinationFile()
    {
        var existingBytes = new byte[] { 2, 4, 6 };
        await System.IO.File.WriteAllBytesAsync(Path.Combine(destinationDirectory, "track.wav"), existingBytes);

        var response = await library.MoveTracksAsync([trackId], destinationFolderId, CancellationToken.None);

        Assert.True(response.Results.Single().Succeeded);
        Assert.Equal(existingBytes, await System.IO.File.ReadAllBytesAsync(Path.Combine(destinationDirectory, "track.wav")));
        Assert.Equal(audioBytes, await System.IO.File.ReadAllBytesAsync(Path.Combine(destinationDirectory, "track (2).wav")));
        Assert.False(System.IO.File.Exists(Path.Combine(sourceDirectory, "track.wav")));
    }

    [Fact]
    public async Task RemoveRootClearsCatalogButLeavesMusicFilesUntouched()
    {
        var rootFile = Path.Combine(sourceDirectory, "track.wav");

        Assert.True(await library.RemoveRootAsync(sourceRootId, CancellationToken.None));

        Assert.False(await database.SourceRoots.AnyAsync(root => root.Id == sourceRootId));
        Assert.False(await database.Folders.AnyAsync(folder => folder.SourceRootId == sourceRootId));
        Assert.False(await database.Tracks.AnyAsync(track => track.SourceRootId == sourceRootId));
        Assert.True(System.IO.File.Exists(rootFile));
        Assert.Equal(audioBytes, await System.IO.File.ReadAllBytesAsync(rootFile));
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MusicPlayer.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
