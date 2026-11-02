using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;
using MusicPlayer.Api.Services.Identification;

namespace MusicPlayer.Api.Tests;

public sealed class MusicLibraryScanTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private MusicDbContext database = null!;
    private MusicLibraryService library = null!;
    private StubMusicBrainzClient musicBrainz = null!;
    private ProviderSettingsService providerSettings = null!;
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
        providerSettings = new ProviderSettingsService(
            database,
            new EphemeralDataProtectionProvider(),
            new ConfigurationBuilder().AddInMemoryCollection().Build());
        library = new MusicLibraryService(
            database,
            new TestWebHostEnvironment(),
            musicBrainz,
            NullLogger<MusicLibraryService>.Instance,
            new MusicLibraryBrowseService(database),
            [],
            providerSettings);
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
    public async Task ArtistAndAlbumLookupsFindTracksWithUnicodeArtistNames()
    {
        var albumDirectory = Path.Combine(sourceDirectory, "Unicode Artist Album");
        Directory.CreateDirectory(albumDirectory);
        var audioPath = Path.Combine(albumDirectory, "track.wav");
        await CreateWaveFileAsync(audioPath);
        using (var audio = TagLib.File.Create(audioPath))
        {
            audio.Tag.Title = "Unicode Artist Track";
            audio.Tag.Performers = ["Motörhead"];
            audio.Tag.AlbumArtists = ["Motörhead"];
            audio.Tag.Album = "After Hours";
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Unicode Artist Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);

        Assert.Single(await library.GetArtistTracksAsync("Motörhead", CancellationToken.None));
        Assert.Single(await library.GetAlbumTracksAsync("Motörhead", "After Hours", CancellationToken.None));
    }

    [Fact]
    public async Task TrackPageFiltersAndLimitsResults()
    {
        var albumDirectory = Path.Combine(sourceDirectory, "Paged Album");
        Directory.CreateDirectory(albumDirectory);
        var firstTrack = Path.Combine(albumDirectory, "first.wav");
        var secondTrack = Path.Combine(albumDirectory, "second.wav");
        var secondAlbumDirectory = Path.Combine(sourceDirectory, "Second Album");
        Directory.CreateDirectory(secondAlbumDirectory);
        var thirdTrack = Path.Combine(secondAlbumDirectory, "third.wav");
        await CreateWaveFileAsync(firstTrack);
        await CreateWaveFileAsync(secondTrack);
        await CreateWaveFileAsync(thirdTrack);
        using (var audio = TagLib.File.Create(firstTrack))
        {
            audio.Tag.Title = "Matching Track";
            audio.Tag.Performers = ["Page Artist"];
            audio.Tag.Album = "Paged Album";
            audio.Save();
        }

        using (var audio = TagLib.File.Create(secondTrack))
        {
            audio.Tag.Title = "Other Track";
            audio.Tag.Performers = ["Page Artist"];
            audio.Tag.Album = "Paged Album";
            audio.Save();
        }

        using (var audio = TagLib.File.Create(thirdTrack))
        {
            audio.Tag.Title = "Third Track";
            audio.Tag.Performers = ["Second Artist"];
            audio.Tag.Album = "Second Album";
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Paged Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);

        var result = await library.GetTracksPageAsync("matching", page: 1, pageSize: 1, CancellationToken.None);
        var folderPage = await library.GetTracksPageAsync(
            null,
            page: 1,
            pageSize: 10,
            CancellationToken.None,
            folderId: result.Items.Single().FolderId);
        var folderTracks = await library.SearchTracksAsync(
            null,
            CancellationToken.None,
            result.Items.Single().FolderId);
        var pathTracks = await library.GetTracksPageAsync(
            null,
            page: 1,
            pageSize: 10,
            CancellationToken.None,
            sourceRootId: result.Items.Single().SourceRootId,
            folderPath: "Paged Album");
        var albums = await library.GetAlbumsPageAsync("Paged", page: 1, pageSize: 1, CancellationToken.None);
        var artists = await library.GetArtistsPageAsync("Page", page: 1, pageSize: 1, CancellationToken.None);
        var artistTracks = await library.GetArtistTracksAsync("Page Artist", CancellationToken.None);
        var albumTracksByTrackId = await library.GetAlbumTracksByTrackIdAsync(
            result.Items.Single().Id,
            CancellationToken.None);
        var albumTracks = await library.GetAlbumTracksAsync("Page Artist", "Paged Album", CancellationToken.None);
        var firstAlbumPage = await library.GetAlbumsPageAsync(null, page: 1, pageSize: 1, CancellationToken.None);
        var secondAlbumPage = await library.GetAlbumsPageAsync(null, page: 2, pageSize: 1, CancellationToken.None);
        var firstArtistPage = await library.GetArtistsPageAsync(null, page: 1, pageSize: 1, CancellationToken.None);
        var secondArtistPage = await library.GetArtistsPageAsync(null, page: 2, pageSize: 1, CancellationToken.None);
        var randomTracks = await library.GetTracksPageAsync(null, page: 1, pageSize: 6, CancellationToken.None, random: true);
        var randomAlbums = await library.GetAlbumsPageAsync(null, page: 1, pageSize: 6, CancellationToken.None, random: true);
        var randomArtists = await library.GetArtistsPageAsync(null, page: 1, pageSize: 6, CancellationToken.None, random: true);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Single(result.Items);
        Assert.Equal("Matching Track", result.Items[0].Title);
        Assert.Equal(2, folderPage.TotalCount);
        Assert.Equal(2, folderTracks.Count);
        Assert.All(folderTracks, track => Assert.Equal(result.Items[0].FolderId, track.FolderId));
        Assert.Equal(2, pathTracks.TotalCount);
        Assert.Equal(1, albums.TotalCount);
        Assert.Equal(2, albums.Items.Single().TrackCount);
        Assert.Equal("Paged Album", albums.Items.Single().Album);
        Assert.Equal(1, artists.TotalCount);
        Assert.Equal(2, artists.Items.Single().TrackCount);
        Assert.Equal(1, artists.Items.Single().AlbumCount);
        Assert.Equal(2, artistTracks.Count);
        Assert.All(artistTracks, track => Assert.Equal("Page Artist", track.Artist));
        Assert.Equal(2, albumTracksByTrackId.Count);
        Assert.Equal(2, albumTracks.Count);
        Assert.Equal(2, firstAlbumPage.TotalCount);
        Assert.Equal("Paged Album", firstAlbumPage.Items.Single().Album);
        Assert.Equal("Second Album", secondAlbumPage.Items.Single().Album);
        Assert.Equal(2, firstArtistPage.TotalCount);
        Assert.Equal("Page Artist", firstArtistPage.Items.Single().Artist);
        Assert.Equal("Second Artist", secondArtistPage.Items.Single().Artist);
        Assert.Equal(3, randomTracks.Items.Count);
        Assert.Equal(2, randomAlbums.Items.Count);
        Assert.Equal(2, randomArtists.Items.Count);
    }

    [Fact]
    public async Task ArtistPageGroupsTracksByAlbumArtistInsteadOfPerTrackCredits()
    {
        var albumDirectory = Path.Combine(sourceDirectory, "Featured Album");
        Directory.CreateDirectory(albumDirectory);
        var performers = new[]
        {
            "Cojones feat. Pavel Miholjević",
            "Cojones feat. Guest Singer",
            "Cojones",
        };
        for (var index = 0; index < performers.Length; index++)
        {
            var audioPath = Path.Combine(albumDirectory, $"track-{index}.wav");
            await CreateWaveFileAsync(audioPath);
            using var audio = TagLib.File.Create(audioPath);
            audio.Tag.Title = $"Track {index}";
            audio.Tag.Performers = [performers[index]];
            audio.Tag.AlbumArtists = ["Cojones"];
            audio.Tag.Album = "Featured Album";
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Featured Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);

        var artists = await library.GetArtistsPageAsync(null, 1, 10, CancellationToken.None);
        var albumArtistTracks = await library.GetArtistTracksAsync("Cojones", CancellationToken.None);
        var performerArtists = await library.GetArtistsPageAsync("Pavel", 1, 10, CancellationToken.None);

        var artist = Assert.Single(artists.Items);
        Assert.Equal("Cojones", artist.Artist);
        Assert.Equal(3, artist.TrackCount);
        Assert.Equal(1, artist.AlbumCount);
        Assert.Equal(3, albumArtistTracks.Count);
        Assert.All(albumArtistTracks, track => Assert.Equal("Cojones", track.AlbumArtist));
        Assert.Empty(performerArtists.Items);
    }

    [Fact]
    public async Task FolderBrowserListsPhysicalDirectoriesAndFilesBeforeScanning()
    {
        var artistDirectory = Path.Combine(sourceDirectory, "Artist");
        var albumDirectory = Path.Combine(artistDirectory, "Album");
        Directory.CreateDirectory(albumDirectory);
        await System.IO.File.WriteAllTextAsync(Path.Combine(sourceDirectory, "readme.txt"), "music library");
        await System.IO.File.WriteAllTextAsync(Path.Combine(albumDirectory, "track.mp3"), "unscanned track");

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Browse Music",
            Path = sourceDirectory,
        }, CancellationToken.None);

        var folderRoots = await library.GetLibraryFolderRootsAsync(CancellationToken.None);
        var rootContents = await library.BrowseLibraryFolderAsync(root.Id, "", CancellationToken.None);
        var artistContents = await library.BrowseLibraryFolderAsync(root.Id, "Artist", CancellationToken.None);
        var albumContents = await library.BrowseLibraryFolderAsync(root.Id, "Artist/Album", CancellationToken.None);

        Assert.Equal("Browse Music", Assert.Single(folderRoots).Name);
        Assert.NotNull(rootContents);
        Assert.Equal("Artist", Assert.Single(rootContents.Folders).Name);
        Assert.Equal("readme.txt", Assert.Single(rootContents.Files).Name);
        Assert.NotNull(artistContents);
        Assert.Equal("Album", Assert.Single(artistContents.Folders).Name);
        Assert.NotNull(albumContents);
        Assert.Equal("track.mp3", Assert.Single(albumContents.Files).Name);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            library.BrowseLibraryFolderAsync(root.Id, "../outside", CancellationToken.None));
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
    public async Task ArtistDescriptionAndImagesCanBeEditedAndPersisted()
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

        await library.UpdateArtistDescriptionAsync(new MetadataDescriptionRequest
        {
            Artist = "Metadata Artist",
            Description = "  Artist biography.  ",
        }, CancellationToken.None);
        var track = Assert.Single(await library.GetTracksAsync(CancellationToken.None));
        Assert.Equal("Metadata Track", track.Title);
        var artistMetadata = await library.GetArtistMetadataAsync("Metadata Artist", CancellationToken.None);
        Assert.Equal("Artist biography.", artistMetadata.Description);
        Assert.True(artistMetadata.ImageMissing);

        database.Tracks.Add(new MusicTrack
        {
            SourceRootId = root.Id,
            FolderId = track.FolderId,
            RelativePath = "Metadata Album/reference.wav",
            Title = "Reference Track",
            Artist = "Referenced Artist",
            Album = "Reference Album",
        });
        await database.SaveChangesAsync();
        await library.UpdateArtistDescriptionAsync(new MetadataDescriptionRequest
        {
            Artist = "Metadata Artist",
            Description = "Bio [a=Referenced Artist] [a=Missing Artist]",
        }, CancellationToken.None);
        artistMetadata = await library.GetArtistMetadataAsync("Metadata Artist", CancellationToken.None);
        Assert.Equal(["Referenced Artist"], artistMetadata.ReferencedArtists);

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
            artistMetadata = await library.GetArtistMetadataAsync("Metadata Artist", CancellationToken.None);
            Assert.False(artistMetadata.ImageMissing);
            Assert.Contains("/api/library/artists/", artistMetadata.ImageUrl);
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
        Assert.Equal(1, musicBrainz.ArtistLookups);
        Assert.Equal(1, musicBrainz.AlbumLookups);
    }

    [Fact]
    public async Task RefreshRootMetadataProcessesDistinctArtistsAndAlbums()
    {
        var albumDirectory = Path.Combine(sourceDirectory, "Batch Album");
        Directory.CreateDirectory(albumDirectory);
        foreach (var fileName in new[] { "one.wav", "two.wav" })
        {
            var audioPath = Path.Combine(albumDirectory, fileName);
            await CreateWaveFileAsync(audioPath);
            using var audio = TagLib.File.Create(audioPath);
            audio.Tag.Performers = ["Batch Performer"];
            audio.Tag.Album = "Batch Album";
            audio.Tag.AlbumArtists = ["Batch Album Artist"];
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Batch Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        var progress = new List<(int Completed, int Total)>();

        var result = await library.RefreshRootMetadataAsync(
            root.Id,
            (completed, total, _) => progress.Add((completed, total)),
            CancellationToken.None);

        Assert.Equal(2, result.ArtistsProcessed);
        Assert.Equal(1, result.AlbumsProcessed);
        Assert.Equal(0, result.ArtistImagesUpdated);
        Assert.Equal(0, result.AlbumImagesUpdated);
        Assert.Equal(2, musicBrainz.ArtistLookups);
        Assert.Equal(1, musicBrainz.AlbumLookups);
        Assert.Equal((3, 3), progress[^1]);
    }

    [Fact]
    public async Task AlbumMetadataRefreshUsesTheConfiguredProviderToResolveAlbumNames()
    {
        var provider = new StubAlbumProvider();
        await providerSettings.UpdateAsync(
            "discogs",
            new Dictionary<string, string?> { ["token"] = "test-token" },
            CancellationToken.None);
        await providerSettings.UpdateDefaultAlbumProviderAsync("discogs", CancellationToken.None);
        musicBrainz.AlbumResult = new MusicBrainzResult("resolved-mbid", "Resolved description.", null, null);
        library = new MusicLibraryService(
            database,
            new TestWebHostEnvironment(),
            musicBrainz,
            NullLogger<MusicLibraryService>.Instance,
            new MusicLibraryBrowseService(database),
            [provider],
            providerSettings);

        var result = await library.RefreshAlbumMetadataAsync(
            "Tagged Artist",
            "Tagged Album",
            CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal(1, provider.SearchCount);
        Assert.Equal("Matched Artist", musicBrainz.LastAlbumArtist);
        Assert.Equal("Matched Album", musicBrainz.LastAlbumTitle);
    }

    [Fact]
    public async Task SelectedAlbumCandidateCanBePreviewedBeforeItIsSaved()
    {
        var provider = new StubAlbumProvider();
        await providerSettings.UpdateAsync(
            "discogs",
            new Dictionary<string, string?> { ["token"] = "test-token" },
            CancellationToken.None);
        musicBrainz.AlbumResult = new MusicBrainzResult("resolved-mbid", "Candidate description.", null, null);
        library = new MusicLibraryService(
            database,
            new TestWebHostEnvironment(),
            musicBrainz,
            NullLogger<MusicLibraryService>.Instance,
            new MusicLibraryBrowseService(database),
            [provider],
            providerSettings);

        var preview = await library.PreviewAlbumMetadataAsync(
            "Tagged Artist",
            "Tagged Album",
            "discogs",
            "match-id",
            CancellationToken.None);

        Assert.True(preview.Found);
        Assert.Equal("Candidate description.", preview.Description);
        Assert.Empty(await database.AlbumMetadata.ToListAsync());

        var saved = await library.RefreshAlbumMetadataAsync(
            "Tagged Artist",
            "Tagged Album",
            "discogs",
            "match-id",
            CancellationToken.None);

        Assert.True(saved.Found);
        Assert.Equal("Candidate description.", saved.Description);
        Assert.Equal(2, provider.GetCount);
    }

    [Fact]
    public async Task ArtistRefreshSavesTheExplicitlySelectedProviderCandidate()
    {
        var provider = new StubArtistProvider();
        library = new MusicLibraryService(
            database,
            new TestWebHostEnvironment(),
            musicBrainz,
            NullLogger<MusicLibraryService>.Instance,
            new MusicLibraryBrowseService(database),
            [],
            null,
            null,
            [provider]);

        var result = await library.RefreshArtistMetadataAsync(
            "Tagged Artist",
            "discogs",
            "artist-id",
            CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal("Selected artist profile.", result.Description);
        Assert.Equal("Selected artist profile.", (await database.ArtistMetadata.SingleAsync()).Description);
        Assert.Equal(0, musicBrainz.ArtistLookups);
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

    private sealed class StubAlbumProvider : IAlbumProvider
    {
        public string Id => "discogs";
        public string Name => "Discogs";
        public bool IsConfigured => true;
        public int SearchCount { get; private set; }
        public int GetCount { get; private set; }

        private AlbumMatchResponse Match =>
            new(Id, "match-id", "Matched Artist", "Matched Album", 2024, 10, null, null);

        public Task<IReadOnlyList<AlbumMatchResponse>> SearchAsync(
            string? artist,
            string? album,
            int? year,
            CancellationToken cancellationToken)
        {
            SearchCount++;
            return Task.FromResult<IReadOnlyList<AlbumMatchResponse>>([Match]);
        }

        public Task<AlbumMatchDetailResponse?> GetAsync(string id, CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult<AlbumMatchDetailResponse?>(
                new AlbumMatchDetailResponse(Match, "Rock", null, []));
        }
    }

    private sealed class StubArtistProvider : IArtistProvider
    {
        public string Id => "discogs";
        public string Name => "Discogs";
        public bool IsConfigured => true;

        public Task<IReadOnlyList<ArtistMatchResponse>> SearchArtistsAsync(
            string artist,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtistMatchResponse>>(
                [new ArtistMatchResponse(Id, "artist-id", "Selected Artist", null, null)]);

        public Task<ArtistProviderDetail?> GetArtistAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult<ArtistProviderDetail?>(
                new ArtistProviderDetail(
                    new ArtistMatchResponse(Id, id, "Selected Artist", null, null),
                    "Selected artist profile.",
                    null,
                    null,
                    null,
                    null));
    }
}
