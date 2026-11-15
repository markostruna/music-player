using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;
using MusicPlayer.Api.Services.Identification;
using SkiaSharp;

namespace MusicPlayer.Api.Tests;

public sealed class MusicLibraryScanTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private MusicDbContext database = null!;
    private MusicLibraryService library = null!;
    private StubMusicBrainzClient musicBrainz = null!;
    private ProviderSettingsService providerSettings = null!;
    private ImageWebpService imageWebp = null!;
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
        imageWebp = new ImageWebpService(
            NullLogger<ImageWebpService>.Instance,
            Path.Combine(testDirectory, "webp-cache"));
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
            imageWebp,
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
    public async Task RecentAlbumsUseFirstImportTimeAndReturnOnlySevenNewest()
    {
        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        Assert.Empty(await library.GetRecentAlbumsAsync(CancellationToken.None));

        for (var index = 0; index < 9; index++)
        {
            var albumDirectory = Path.Combine(sourceDirectory, $"Album {index}");
            Directory.CreateDirectory(albumDirectory);
            var path = Path.Combine(albumDirectory, "track.wav");
            await CreateWaveFileAsync(path);
            using var audio = TagLib.File.Create(path);
            audio.Tag.Performers = ["Shared Artist"];
            audio.Tag.Album = $"Album {index}";
            audio.Save();
        }

        var beforeScan = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await library.ScanAsync(root.Id, CancellationToken.None);
        var tracks = await database.Tracks.ToListAsync();
        Assert.All(tracks, track => Assert.True(track.ImportedAtUnixMilliseconds >= beforeScan));
        var firstImport = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var track in tracks)
        {
            var index = int.Parse(track.Album["Album ".Length..]);
            track.ImportedAtUnixMilliseconds = firstImport.AddDays(index).ToUnixTimeMilliseconds();
        }
        await database.SaveChangesAsync();

        var extraPath = Path.Combine(sourceDirectory, "Album 0", "extra.wav");
        await CreateWaveFileAsync(extraPath);
        using (var audio = TagLib.File.Create(extraPath))
        {
            audio.Tag.Performers = ["Shared Artist"];
            audio.Tag.Album = "Album 0";
            audio.Save();
        }
        await library.ScanAsync(root.Id, CancellationToken.None);
        database.ChangeTracker.Clear();
        var oldTrack = await database.Tracks.SingleAsync(track => track.RelativePath == "Album 0/track.wav");
        oldTrack.IsAvailable = false;
        await database.SaveChangesAsync();

        var recent = await library.GetRecentAlbumsAsync(CancellationToken.None);
        Assert.Equal(7, recent.Count);
        Assert.Equal(Enumerable.Range(2, 7).Reverse().Select(index => $"Album {index}"),
            recent.Select(album => album.Album));
        Assert.Equal(firstImport.AddDays(8), recent[0].ImportedAt);
        var albumPage = await library.GetAlbumsPageAsync("Album 0", 1, 10, CancellationToken.None);
        Assert.Equal(firstImport, Assert.Single(albumPage.Items).ImportedAt);
        var artists = await library.GetArtistsPageAsync("Shared Artist", 1, 10, CancellationToken.None);
        Assert.Equal(firstImport, Assert.Single(artists.Items).ImportedAt);
    }

    [Fact]
    public async Task DatabaseCleanupRemovesMissingEntriesButKeepsExistingUnavailableTracks()
    {
        var existingDirectory = Path.Combine(sourceDirectory, "Existing");
        var missingDirectory = Path.Combine(sourceDirectory, "Missing");
        Directory.CreateDirectory(existingDirectory);
        Directory.CreateDirectory(missingDirectory);
        var existing = Path.Combine(existingDirectory, "existing.wav");
        var missingFile = Path.Combine(existingDirectory, "missing.wav");
        var missingAlbumFile = Path.Combine(missingDirectory, "album.wav");
        await CreateWaveFileAsync(existing);
        await CreateWaveFileAsync(missingFile);
        await CreateWaveFileAsync(missingAlbumFile);
        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        var existingTrack = await database.Tracks.SingleAsync(track => track.RelativePath == "Existing/existing.wav");
        existingTrack.IsAvailable = false;
        await database.SaveChangesAsync();
        System.IO.File.Delete(missingFile);
        System.IO.File.Delete(missingAlbumFile);
        Directory.Delete(missingDirectory);

        var result = await library.CleanDatabaseAsync((_, _, _) => { }, CancellationToken.None);

        Assert.Equal(2, result.RemovedTracks);
        Assert.Equal(1, result.RemovedFolders);
        Assert.Empty(result.SkippedRoots);
        Assert.Equal(existingTrack.Id, (await database.Tracks.SingleAsync()).Id);
        Assert.True(System.IO.File.Exists(existing));
        Assert.True(await database.Folders.AnyAsync(folder => folder.RelativePath == "Existing"));
        Assert.True(await database.SourceRoots.AnyAsync(item => item.Id == root.Id));
    }

    [Fact]
    public async Task DatabaseCleanupSkipsOfflineRootWithoutRemovingEntries()
    {
        var audioPath = Path.Combine(sourceDirectory, "track.wav");
        await CreateWaveFileAsync(audioPath);
        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Offline",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        Directory.Move(sourceDirectory, Path.Combine(testDirectory, "offline"));

        var result = await library.CleanDatabaseAsync((_, _, _) => { }, CancellationToken.None);

        Assert.Equal(0, result.RemovedTracks);
        Assert.Equal(0, result.RemovedFolders);
        Assert.Equal(["Offline"], result.SkippedRoots);
        Assert.Single(await database.Tracks.ToListAsync());
        Assert.Single(await database.Folders.ToListAsync());
    }

    [Fact]
    public async Task DatabaseCleanupSkipsEntireRootWhenCatalogPathIsUnsafe()
    {
        var firstPath = Path.Combine(sourceDirectory, "first.wav");
        var secondPath = Path.Combine(sourceDirectory, "second.wav");
        await CreateWaveFileAsync(firstPath);
        await CreateWaveFileAsync(secondPath);
        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Unsafe",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        var unsafeTrack = await database.Tracks.SingleAsync(track => track.RelativePath == "second.wav");
        unsafeTrack.RelativePath = "../outside.wav";
        await database.SaveChangesAsync();
        System.IO.File.Delete(firstPath);

        var result = await library.CleanDatabaseAsync((_, _, _) => { }, CancellationToken.None);

        Assert.Equal(0, result.RemovedTracks);
        Assert.Equal(["Unsafe"], result.SkippedRoots);
        Assert.Equal(2, await database.Tracks.CountAsync());
        Assert.True(System.IO.File.Exists(secondPath));
    }

    [Fact]
    public async Task DatabaseCleanupOperationReportsRemovedAndSkippedEntries()
    {
        var audioPath = Path.Combine(sourceDirectory, "track.wav");
        await CreateWaveFileAsync(audioPath);
        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        System.IO.File.Delete(audioPath);
        database.SourceRoots.Add(new MusicSourceRoot
        {
            Name = "Offline",
            Path = Path.Combine(testDirectory, "offline"),
        });
        await database.SaveChangesAsync();

        using var services = new ServiceCollection()
            .AddSingleton<IMusicLibraryService>(library)
            .BuildServiceProvider();
        using var manager = new LibraryOperationManager(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LibraryOperationManager>.Instance);
        try
        {
            var operation = manager.TryStart(0, "database", LibraryOperationManager.DatabaseCleanupOperation);
            Assert.NotNull(operation);
            Assert.Null(manager.TryStart(root.Id, root.Name, LibraryOperationManager.ScanOperation));
            await manager.StartAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            LibraryOperationStatus? status;
            do
            {
                await Task.Delay(20, timeout.Token);
                status = manager.Get(operation.Id);
            }
            while (status?.State is "queued" or "running");

            Assert.NotNull(status);
            Assert.Equal("completed", status.State);
            Assert.Contains("Removed 1 tracks", status.Message);
            Assert.Contains("Skipped 1", status.Message);
            Assert.Contains("Offline", status.Message);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
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
        var coverBytes = CreatePngImage();
        var coverUpdate = await library.UpdateCoverAsync(new[] { rescannedTrack.Id }, coverBytes, CancellationToken.None);
        var cover = await library.ResolveCoverAsync(rescannedTrack.Id, CancellationToken.None);

        Assert.True(coverUpdate.Results.Single().Succeeded);
        AssertWebp(cover);

        var folderCoverPath = Path.Combine(albumDirectory, "Folder.jpg");
        await System.IO.File.WriteAllBytesAsync(folderCoverPath, coverBytes);
        var folderCover = await library.ResolveCoverAsync(rescannedTrack.Id, CancellationToken.None);
        AssertWebp(folderCover);

        System.IO.File.Delete(folderCoverPath);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(albumDirectory, "cover.png"), coverBytes);
        var alternateCover = await library.ResolveCoverAsync(rescannedTrack.Id, CancellationToken.None);
        AssertWebp(alternateCover);
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

        var imageBytes = CreatePngImage();
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
            cleanupPaths.AddRange(cleanupPaths.ToArray().Select(path => Path.ChangeExtension(path, ".webp")));
            Assert.False(artist.ImageMissing);
            Assert.False(album.ImageMissing);
            artistMetadata = await library.GetArtistMetadataAsync("Metadata Artist", CancellationToken.None);
            Assert.False(artistMetadata.ImageMissing);
            Assert.Contains("/api/library/artists/", artistMetadata.ImageUrl);
            AssertWebp(await library.ResolveArtistImageAsync(artist.Key, CancellationToken.None));
            AssertWebp(await library.ResolveCoverAsync(track.Id, CancellationToken.None));
            Assert.Equal(imageBytes, await System.IO.File.ReadAllBytesAsync(cleanupPaths[0]));
            AssertWebpBytes(await System.IO.File.ReadAllBytesAsync(cleanupPaths[2]));
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
    public async Task ImportOperationScansAndRefreshesOnlyImportedAlbum()
    {
        var oldAlbum = Path.Combine(sourceDirectory, "Artist", "Album Extended");
        var newAlbum = Path.Combine(sourceDirectory, "Artist", "Album");
        Directory.CreateDirectory(oldAlbum);
        var oldTrackPath = Path.Combine(oldAlbum, "old.wav");
        await CreateWaveFileAsync(oldTrackPath);
        using (var audio = TagLib.File.Create(oldTrackPath))
        {
            audio.Tag.Performers = ["Old Artist"];
            audio.Tag.Album = "Old Album";
            audio.Save();
        }

        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        var oldTrack = await database.Tracks.SingleAsync();
        var oldScannedAt = oldTrack.ScannedAt;
        System.IO.File.Delete(oldTrackPath);

        Directory.CreateDirectory(Path.Combine(newAlbum, "Disc 1"));
        var newTrackPath = Path.Combine(newAlbum, "Disc 1", "new.wav");
        await CreateWaveFileAsync(newTrackPath);
        using (var audio = TagLib.File.Create(newTrackPath))
        {
            audio.Tag.Performers = ["New Artist"];
            audio.Tag.AlbumArtists = ["New Artist"];
            audio.Tag.Album = "New Album";
            audio.Save();
        }

        using var services = new ServiceCollection()
            .AddSingleton<IMusicLibraryService>(library)
            .BuildServiceProvider();
        using var manager = new LibraryOperationManager(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LibraryOperationManager>.Instance);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(manager.TryBeginLibraryMutation());
            var operation = manager.StartReservedImportOperation(
                root.Id, root.Name, "Artist/Album", sourceRetained: false);
            Assert.NotNull(operation);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            LibraryOperationStatus? status;
            do
            {
                await Task.Delay(20, timeout.Token);
                status = manager.Get(operation.Id);
            }
            while (status?.State is "queued" or "running");

            Assert.NotNull(status);
            Assert.Equal("completed", status.State);
            Assert.Contains("1 tracks", status.Message);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }

        database.ChangeTracker.Clear();
        var tracks = await database.Tracks.OrderBy(track => track.RelativePath).ToListAsync();
        Assert.Equal(2, tracks.Count);
        Assert.Contains(tracks, track => track.RelativePath == "Artist/Album/Disc 1/new.wav" && track.IsAvailable);
        var unchanged = Assert.Single(tracks, track => track.Id == oldTrack.Id);
        Assert.True(unchanged.IsAvailable);
        Assert.Equal(oldScannedAt, unchanged.ScannedAt);
        Assert.Equal(1, musicBrainz.ArtistLookups);
        Assert.Equal(1, musicBrainz.AlbumLookups);
        Assert.Equal("New Artist", musicBrainz.LastAlbumArtist);
        Assert.Equal("New Album", musicBrainz.LastAlbumTitle);
    }

    [Fact]
    public async Task FolderScanRejectsMissingOrRootPathsWithoutChangingExistingTracks()
    {
        var audioPath = Path.Combine(sourceDirectory, "track.wav");
        await CreateWaveFileAsync(audioPath);
        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => library.ScanFolderAsync(root.Id, "", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => library.ScanFolderAsync(root.Id, "../outside", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => library.ScanFolderAsync(root.Id, "missing", CancellationToken.None));

        Assert.True((await database.Tracks.SingleAsync()).IsAvailable);
    }

    [Fact]
    public async Task FolderRescanMarksOnlyMissingTracksInsideThatFolderUnavailable()
    {
        var album = Path.Combine(sourceDirectory, "Artist", "Album");
        var sibling = Path.Combine(sourceDirectory, "Artist", "Album Extra");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(sibling);
        var removedTrack = Path.Combine(album, "removed.wav");
        var siblingTrack = Path.Combine(sibling, "sibling.wav");
        await CreateWaveFileAsync(removedTrack);
        await CreateWaveFileAsync(siblingTrack);
        var root = await library.AddRootAsync(new CreateSourceRootRequest
        {
            Name = "Music",
            Path = sourceDirectory,
        }, CancellationToken.None);
        await library.ScanAsync(root.Id, CancellationToken.None);
        System.IO.File.Delete(removedTrack);
        System.IO.File.Delete(siblingTrack);

        var result = await library.ScanFolderAsync(root.Id, "Artist/Album", CancellationToken.None);

        Assert.Equal(0, result.DiscoveredTracks);
        Assert.False((await database.Tracks.SingleAsync(track => track.RelativePath == "Artist/Album/removed.wav")).IsAvailable);
        Assert.True((await database.Tracks.SingleAsync(track => track.RelativePath == "Artist/Album Extra/sibling.wav")).IsAvailable);
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
            imageWebp,
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
            imageWebp,
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
            imageWebp,
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
        var image = CreatePngImage();
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
        AssertWebp(await library.ResolveArtistImageAsync(artist.Key, CancellationToken.None));
        AssertWebp(await library.ResolveCoverAsync(
            Assert.Single(await library.GetTracksAsync(CancellationToken.None)).Id,
            CancellationToken.None));
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
        var image = CreatePngImage();
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

    private static void AssertWebp(MusicPlayer.Api.Contracts.CoverImage? image)
    {
        Assert.NotNull(image);
        Assert.Equal("image/webp", image.ContentType);
        AssertWebpBytes(image.Data);
    }

    private static void AssertWebpBytes(byte[] image)
    {
        Assert.True(image.Length >= 12);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(image, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(image, 8, 4));
        using var bitmap = SKBitmap.Decode(image);
        Assert.NotNull(bitmap);
    }

    private static byte[] CreatePngImage()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Crimson);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray() ?? throw new InvalidOperationException("The test PNG could not be encoded.");
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
