using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class UnscannedFolderServiceTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private MusicDbContext database = null!;
    private UnscannedFolderService service = null!;
    private string testDirectory = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        database = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(connection)
            .Options);
        await database.Database.EnsureCreatedAsync();
        testDirectory = Path.Combine(Path.GetTempPath(), $"afterhours-unscanned-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        service = new UnscannedFolderService(
            database,
            new TestWebHostEnvironment(),
            NullLogger<UnscannedFolderService>.Instance);
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
    public async Task RegisteredFolderIsNotScannedAndSupportsBrowsingAndRenaming()
    {
        var source = Path.Combine(testDirectory, "inbox");
        var album = Path.Combine(source, "Album");
        Directory.CreateDirectory(album);
        await File.WriteAllTextAsync(Path.Combine(album, "track.flac"), "audio");
        await File.WriteAllTextAsync(
            Path.Combine(album, "Album.cue"),
            "FILE \"track.flac\" WAVE\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00\r\n");
        var folder = await service.AddAsync(new CreateUnscannedFolderRequest
        {
            Name = "Inbox",
            Path = source,
        }, CancellationToken.None);

        var root = await database.SourceRoots.SingleAsync();
        Assert.False(root.IsEnabled);
        var contents = await service.BrowseAsync(folder.Id, "", CancellationToken.None);
        Assert.Equal("Album", Assert.Single(contents!.Folders).Name);

        await service.RenameFolderAsync(folder.Id, "Album", new RenameUnscannedFolderRequest
        {
            Name = "Renamed",
        }, CancellationToken.None);
        await service.RenameTrackAsync(folder.Id, "Renamed/track.flac", new RenameUnscannedTrackRequest
        {
            Name = "01 - Track",
        }, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(source, "Renamed", "01 - Track.flac")));
        Assert.False(File.Exists(Path.Combine(source, "Renamed", "track.flac")));
        Assert.Contains("FILE \"01 - Track.flac\" WAVE", await File.ReadAllTextAsync(
            Path.Combine(source, "Renamed", "Album.cue")));
    }

    [Fact]
    public async Task AddingUnscannedFolderRejectsOverlapWithScannedSource()
    {
        var scannedPath = Path.Combine(testDirectory, "library");
        var nestedPath = Path.Combine(scannedPath, "inbox");
        Directory.CreateDirectory(nestedPath);
        database.SourceRoots.Add(new MusicSourceRoot { Name = "Library", Path = scannedPath });
        await database.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(
            new CreateUnscannedFolderRequest { Name = "Inbox", Path = nestedPath },
            CancellationToken.None));
    }

    [Fact]
    public async Task ImportCreatesArtistFolderAndMovesAlbumIntoScannedRoot()
    {
        var source = Path.Combine(testDirectory, "inbox");
        var album = Path.Combine(source, "Album");
        var destination = Path.Combine(testDirectory, "library");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(album, "notes.txt"), "album");
        var inbox = await service.AddAsync(new CreateUnscannedFolderRequest
        {
            Name = "Inbox",
            Path = source,
        }, CancellationToken.None);
        var library = new MusicSourceRoot { Name = "Library", Path = destination };
        database.SourceRoots.Add(library);
        await database.SaveChangesAsync();

        var imported = await service.ImportAlbumAsync(inbox.Id, new ImportUnscannedAlbumRequest
        {
            RelativePath = "Album",
            DestinationRootId = library.Id,
            Artist = "Artist",
            Album = "Album",
        }, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "Artist", "Album", "notes.txt")));
        Assert.False(Directory.Exists(album));
        Assert.Equal("Artist/Album", imported.RelativePath);
        Assert.False(imported.SourceRetained);
    }

    [Fact]
    public async Task ExistingDestinationMustBeResolvedBeforeImport()
    {
        var source = Path.Combine(testDirectory, "inbox");
        var sourceAlbum = Path.Combine(source, "Incoming");
        var destination = Path.Combine(testDirectory, "library");
        var existingAlbum = Path.Combine(destination, "Artist", "Album");
        Directory.CreateDirectory(sourceAlbum);
        Directory.CreateDirectory(existingAlbum);
        await File.WriteAllTextAsync(Path.Combine(sourceAlbum, "new.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(existingAlbum, "old.txt"), "old");
        var inbox = await service.AddAsync(new CreateUnscannedFolderRequest
        {
            Name = "Inbox",
            Path = source,
        }, CancellationToken.None);
        var library = new MusicSourceRoot { Name = "Library", Path = destination };
        database.SourceRoots.Add(library);
        await database.SaveChangesAsync();

        var check = await service.CheckAlbumAsync(inbox.Id, new CheckUnscannedAlbumRequest
        {
            RelativePath = "Incoming",
            Artist = "Artist",
            Album = "Album",
            DestinationRootId = library.Id,
        }, CancellationToken.None);
        Assert.True(check.DestinationFolderExists);

        var request = new ImportUnscannedAlbumRequest
        {
            RelativePath = "Incoming",
            DestinationRootId = library.Id,
            Artist = "Artist",
            Album = "Album",
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ImportAlbumAsync(inbox.Id, request, CancellationToken.None));

        await service.ImportAlbumAsync(inbox.Id, request with
        {
            ConflictAction = "rename",
            ExistingAlbumNewName = "Album (old)",
        }, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "Artist", "Album (old)", "old.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "Artist", "Album", "new.txt")));
        Assert.False(Directory.Exists(sourceAlbum));
    }

    [Fact]
    public async Task RenamingExistingAlbumUpdatesItsFolderRecordAndEmbeddedTags()
    {
        var source = Path.Combine(testDirectory, "inbox");
        var sourceAlbum = Path.Combine(source, "Incoming");
        var destination = Path.Combine(testDirectory, "library");
        var existingAlbumPath = Path.Combine(destination, "Artist", "Album");
        Directory.CreateDirectory(sourceAlbum);
        Directory.CreateDirectory(existingAlbumPath);
        await File.WriteAllTextAsync(Path.Combine(sourceAlbum, "new.txt"), "new");
        var oldTrackPath = Path.Combine(existingAlbumPath, "old.wav");
        await CreateWaveFileAsync(oldTrackPath);
        using (var audio = TagLib.File.Create(oldTrackPath))
        {
            audio.Tag.Title = "Old track";
            audio.Tag.Performers = ["Artist"];
            audio.Tag.AlbumArtists = ["Artist"];
            audio.Tag.Album = "Album";
            audio.Save();
        }

        var inbox = await service.AddAsync(new CreateUnscannedFolderRequest
        {
            Name = "Inbox",
            Path = source,
        }, CancellationToken.None);
        var library = new MusicSourceRoot { Name = "Library", Path = destination };
        database.SourceRoots.Add(library);
        await database.SaveChangesAsync();
        var folder = new MusicFolder
        {
            SourceRootId = library.Id,
            Name = "Album",
            RelativePath = "Artist/Album",
        };
        database.Folders.Add(folder);
        await database.SaveChangesAsync();
        var track = new MusicTrack
        {
            SourceRootId = library.Id,
            FolderId = folder.Id,
            RelativePath = "Artist/Album/old.wav",
            Title = "Old track",
            Artist = "Artist",
            AlbumArtist = "Artist",
            Album = "Album",
            IsAvailable = true,
        };
        database.Tracks.Add(track);
        await database.SaveChangesAsync();

        await service.ImportAlbumAsync(inbox.Id, new ImportUnscannedAlbumRequest
        {
            RelativePath = "Incoming",
            DestinationRootId = library.Id,
            Artist = "Artist",
            Album = "Album",
            ConflictAction = "rename",
            ExistingAlbumNewName = "Album (old)",
        }, CancellationToken.None);

        var savedTrack = await database.Tracks.SingleAsync();
        Assert.Equal("Album (old)", savedTrack.Album);
        Assert.Equal("Artist/Album (old)/old.wav", savedTrack.RelativePath);
        using var renamedAudio = TagLib.File.Create(Path.Combine(destination, savedTrack.RelativePath));
        Assert.Equal("Album (old)", renamedAudio.Tag.Album);
        Assert.True(File.Exists(Path.Combine(destination, "Artist", "Album", "new.txt")));
    }

    private static async Task CreateWaveFileAsync(string path)
    {
        const int sampleRate = 8000;
        var samples = new byte[1600];
        await using var file = File.Create(path);
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
