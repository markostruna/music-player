using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class MusicLibraryScanTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private MusicDbContext database = null!;
    private MusicLibraryService library = null!;
    private StubMusicBrainzClient musicBrainz = null!;
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
        musicBrainz = new StubMusicBrainzClient();
        library = new MusicLibraryService(database, new TestWebHostEnvironment(), musicBrainz, NullLogger<MusicLibraryService>.Instance);
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
        Assert.Equal(0, musicBrainz.ArtistLookups);
        Assert.Equal(0, musicBrainz.AlbumLookups);
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

    [Fact]
    public async Task ScanPreservesSlashesInMultiValueArtistTags()
    {
        var albumDirectory = Path.Combine(sourceDirectory, "Back in Black");
        Directory.CreateDirectory(albumDirectory);
        var audioPath = Path.Combine(albumDirectory, "track.wav");
        await CreateWaveFileAsync(audioPath);
        using (var audio = TagLib.File.Create(audioPath))
        {
            audio.Tag.Performers = ["AC", "DC"];
            audio.Tag.AlbumArtists = ["AC", "DC"];
            audio.Tag.Album = "Back in Black";
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Test Music",
            Path = sourceDirectory,
        }, CancellationToken.None);

        await library.ScanAsync(root.Id, CancellationToken.None);

        var track = Assert.Single(await library.GetTracksAsync(CancellationToken.None));
        Assert.Equal("AC/DC", track.Artist);
        Assert.Equal("AC/DC", track.AlbumArtist);
    }

    [Fact]
    public async Task ArtistAndAlbumDescriptionsAndImagesCanBeEditedAndPersisted()
    {
        var albumDirectory = Path.Combine(sourceDirectory, "Metadata Album");
        Directory.CreateDirectory(albumDirectory);
        var audioPath = Path.Combine(albumDirectory, "metadata.wav");
        await CreateWaveFileAsync(audioPath);
        using (var audio = TagLib.File.Create(audioPath))
        {
            audio.Tag.Title = "Metadata Track";
            audio.Tag.Performers = ["Metadata Artist"];
            audio.Tag.Album = "Metadata Album";
            audio.Tag.AlbumArtists = ["Metadata Artist"];
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Metadata Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);

        await library.UpdateMetadataDescriptionAsync(new MetadataDescriptionRequest
        {
            Artist = "Metadata Artist",
            Description = "  Artist biography.  ",
        }, album: false, CancellationToken.None);
        await library.UpdateMetadataDescriptionAsync(new MetadataDescriptionRequest
        {
            Artist = "Metadata Artist",
            Album = "Metadata Album",
            Description = "  Album notes.  ",
        }, album: true, CancellationToken.None);
        var track = Assert.Single(await library.GetTracksAsync(CancellationToken.None));
        Assert.Equal("Artist biography.", track.ArtistDescription);
        Assert.Equal("Album notes.", track.AlbumDescription);

        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a+2QAAAAASUVORK5CYII=");
        var cleanupPaths = new List<string>();
        try
        {
            await library.UpdateMetadataImageAsync(new MetadataImageRequest
            {
                Artist = "Metadata Artist",
                Image = imageBytes,
            }, album: false, CancellationToken.None);
            await library.UpdateMetadataImageAsync(new MetadataImageRequest
            {
                Artist = "Metadata Artist",
                Album = "Metadata Album",
                Image = imageBytes,
            }, album: true, CancellationToken.None);

            var artist = await database.ArtistMetadata.SingleAsync(item => item.Artist == "Metadata Artist");
            var album = await database.AlbumMetadata.SingleAsync(item => item.Album == "Metadata Album");
            cleanupPaths.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Afterhours", "metadata", "artists", artist.ImageFileName!));
            cleanupPaths.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Afterhours", "metadata", "albums", album.ImageFileName!));
            Assert.False(artist.ImageMissing);
            Assert.False(album.ImageMissing);
            Assert.Equal(imageBytes, (await library.ResolveArtistImageAsync(artist.Key, CancellationToken.None))?.Data);
            Assert.Equal(imageBytes, (await library.ResolveCoverAsync(track.Id, CancellationToken.None))?.Data);
        }
        finally
        {
            foreach (var path in cleanupPaths)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task RefreshArtistAndAlbumMetadataFetchesOnlyTheRequestedEntity()
    {
        musicBrainz.ArtistResult = new MusicBrainzResult("artist-mbid", "Fresh artist description.", null, null);
        musicBrainz.AlbumResult = new MusicBrainzResult("album-mbid", "Fresh album description.", null, null);

        var artistResult = await library.RefreshArtistMetadataAsync("Refresh Artist", CancellationToken.None);
        var albumResult = await library.RefreshAlbumMetadataAsync("Refresh Artist", "Refresh Album", CancellationToken.None);
        Assert.True(artistResult.Found);
        Assert.True(albumResult.Found);
        Assert.Equal("Fresh artist description.", artistResult.Description);
        Assert.Equal("Fresh album description.", albumResult.Description);

        var artist = await database.ArtistMetadata.SingleAsync(item => item.Artist == "Refresh Artist");
        var album = await database.AlbumMetadata.SingleAsync(item => item.Album == "Refresh Album");
        Assert.Equal("artist-mbid", artist.MusicBrainzId);
        Assert.Equal("Fresh artist description.", artist.Description);
        Assert.Equal("album-mbid", album.MusicBrainzId);
        Assert.Equal("Fresh album description.", album.Description);
        Assert.Equal(1, musicBrainz.ArtistLookups);
        Assert.Equal(1, musicBrainz.AlbumLookups);
    }

    [Fact]
    public async Task MusicBrainzImagesAreSavedBesideAlbumAndArtistMusic()
    {
        const string artistName = "Refresh Artist";
        const string albumName = "Refresh Album";
        var artistDirectory = Path.Combine(sourceDirectory, artistName);
        var albumDirectory = Path.Combine(artistDirectory, albumName);
        Directory.CreateDirectory(albumDirectory);
        var audioPath = Path.Combine(albumDirectory, "track.wav");
        await CreateWaveFileAsync(audioPath);
        using (var audio = TagLib.File.Create(audioPath))
        {
            audio.Tag.Title = "Refresh Track";
            audio.Tag.Performers = [artistName];
            audio.Tag.Album = albumName;
            audio.Tag.AlbumArtists = [artistName];
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "MusicBrainz Artwork",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a+2QAAAAASUVORK5CYII=");
        musicBrainz.ArtistResult = new MusicBrainzResult("artist-mbid", "Artist description", image, "image/png");
        musicBrainz.AlbumResult = new MusicBrainzResult("album-mbid", "Album description", image, "image/png");

        await library.RefreshArtistMetadataAsync(artistName, CancellationToken.None);
        await library.RefreshAlbumMetadataAsync(artistName, albumName, CancellationToken.None);

        var artist = await database.ArtistMetadata.SingleAsync(item => item.Artist == artistName);
        var album = await database.AlbumMetadata.SingleAsync(item => item.Album == albumName);
        Assert.Equal($"{artistName}/ArtistCover-MusicBrainz.png", artist.ImageRelativePath);
        Assert.Equal($"{artistName}/{albumName}/Cover-MusicBrainz.png", album.ImageRelativePath);
        Assert.True(System.IO.File.Exists(Path.Combine(artistDirectory, "ArtistCover-MusicBrainz.png")));
        Assert.True(System.IO.File.Exists(Path.Combine(albumDirectory, "Cover-MusicBrainz.png")));
        Assert.Equal(image, (await library.ResolveArtistImageAsync(artist.Key, CancellationToken.None))?.Data);
        Assert.Equal(image, (await library.ResolveCoverAsync(
            Assert.Single(await library.GetTracksAsync(CancellationToken.None)).Id,
            CancellationToken.None))?.Data);
    }

    [Fact]
    public async Task MusicBrainzArtistImageFallsBackToTheFolderContainingAnArtistTrack()
    {
        const string artistName = "Fallback Artist";
        var trackDirectory = Path.Combine(sourceDirectory, "Various", "Compilation");
        Directory.CreateDirectory(trackDirectory);
        var audioPath = Path.Combine(trackDirectory, "track.wav");
        await CreateWaveFileAsync(audioPath);
        using (var audio = TagLib.File.Create(audioPath))
        {
            audio.Tag.Title = "Compilation Track";
            audio.Tag.Performers = [artistName];
            audio.Tag.Album = "Compilation Album";
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Compilation Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a+2QAAAAASUVORK5CYII=");
        musicBrainz.ArtistResult = new MusicBrainzResult("artist-mbid", "Artist description", image, "image/png");

        await library.RefreshArtistMetadataAsync(artistName, CancellationToken.None);

        var artist = await database.ArtistMetadata.SingleAsync(item => item.Artist == artistName);
        Assert.Equal("Various/Compilation/ArtistCover-Fallback Artist-MusicBrainz.png", artist.ImageRelativePath);
        Assert.True(System.IO.File.Exists(Path.Combine(trackDirectory, "ArtistCover-Fallback Artist-MusicBrainz.png")));
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
