using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class MusicLibraryScanTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private MusicDbContext database = null!;
    private MusicLibraryService library = null!;
    private string testDirectory = null!;
    private string sourceDirectory = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(connection)
            .Options;
        database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();

        testDirectory = Path.Combine(Path.GetTempPath(), $"afterhours-scan-{Guid.NewGuid():N}");
        sourceDirectory = Path.Combine(testDirectory, "music");
        Directory.CreateDirectory(sourceDirectory);
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
    public async Task ScanReadsWavTagsAndDoesNotDuplicateTracksOnRescan()
    {
        var albumDirectory = Path.Combine(sourceDirectory, "Album Folder");
        Directory.CreateDirectory(albumDirectory);
        var audioPath = Path.Combine(albumDirectory, "track.wav");
        await CreateWaveFileAsync(audioPath);
        using (var audio = TagLib.File.Create(audioPath))
        {
            audio.Tag.Title = "Embedded Title";
            audio.Tag.Performers = ["Embedded Artist"];
            audio.Tag.Album = "Embedded Album";
            audio.Tag.AlbumArtists = ["Embedded Album Artist"];
            audio.Tag.Genres = ["Rock"];
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Test Music",
            Path = sourceDirectory,
        }, CancellationToken.None);

        var firstScan = await library.ScanAsync(root.Id, CancellationToken.None);
        var secondScan = await library.ScanAsync(root.Id, CancellationToken.None);
        var tracks = await library.GetTracksAsync(CancellationToken.None);
        var albums = await library.GetAlbumsAsync(CancellationToken.None);
        var stream = await library.ResolveStreamAsync(tracks.Single().Id, CancellationToken.None);

        Assert.Equal(1, firstScan.DiscoveredTracks);
        Assert.Equal(0, firstScan.UnreadableTracks);
        Assert.Equal(1, secondScan.DiscoveredTracks);
        Assert.Single(tracks);
        Assert.Equal("Embedded Title", tracks[0].Title);
        Assert.Equal("Embedded Artist", tracks[0].Artist);
        Assert.Equal("Embedded Album", tracks[0].Album);
        Assert.Equal("Embedded Album Artist", tracks[0].AlbumArtist);
        Assert.Equal("Rock", tracks[0].Genre);
        Assert.Equal(1, albums.Single().TrackCount);
        Assert.Equal(Path.GetFullPath(audioPath), stream?.FullPath);
        Assert.Equal("audio/wav", stream?.ContentType);

        var update = await library.UpdateMetadataAsync([tracks[0].Id], new MusicMetadataPatch
        {
            Title = "Edited Title",
            Genre = "Jazz",
        }, CancellationToken.None);
        using (var editedAudio = TagLib.File.Create(audioPath))
        {
            Assert.Equal("Edited Title", editedAudio.Tag.Title);
            Assert.Equal("Jazz", editedAudio.Tag.Genres.Single());
        }

        Assert.True(update.Results.Single().Succeeded);
        await library.ScanAsync(root.Id, CancellationToken.None);
        var rescannedTrack = Assert.Single(await library.GetTracksAsync(CancellationToken.None));
        Assert.Equal("Edited Title", rescannedTrack.Title);
        Assert.Equal("Jazz", rescannedTrack.Genre);
        var coverBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a+2QAAAAASUVORK5CYII=");
        var coverUpdate = await library.UpdateCoverAsync(new[] { rescannedTrack.Id }, coverBytes, CancellationToken.None);
        var cover = await library.ResolveCoverAsync(rescannedTrack.Id, CancellationToken.None);

        Assert.True(coverUpdate.Results.Single().Succeeded);
        Assert.Equal("image/png", cover?.ContentType);
        Assert.Equal(coverBytes, cover?.Data);

        byte[] folderCoverBytes = [.. coverBytes, 1];
        var folderCoverPath = Path.Combine(albumDirectory, "Folder.jpg");
        await System.IO.File.WriteAllBytesAsync(folderCoverPath, folderCoverBytes);
        var folderCover = await library.ResolveCoverAsync(rescannedTrack.Id, CancellationToken.None);
        Assert.Equal("image/png", folderCover?.ContentType);
        Assert.Equal(folderCoverBytes, folderCover?.Data);

        System.IO.File.Delete(folderCoverPath);
        byte[] alternateCoverBytes = [.. coverBytes, 2];
        await System.IO.File.WriteAllBytesAsync(Path.Combine(albumDirectory, "cover.png"), alternateCoverBytes);
        var alternateCover = await library.ResolveCoverAsync(rescannedTrack.Id, CancellationToken.None);
        Assert.Equal(alternateCoverBytes, alternateCover?.Data);
    }

    private static async Task CreateWaveFileAsync(string path)
    {
        const int sampleRate = 8000;
        var samples = new byte[1600];
        await using var file = System.IO.File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples.Length);
        writer.Write(samples);
        await file.FlushAsync();
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
