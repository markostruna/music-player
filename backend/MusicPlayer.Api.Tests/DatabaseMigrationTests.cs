using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task PlaylistBackgroundMigrationPreservesExistingPlaylists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options);
        await database.Database.GetService<IMigrator>().MigrateAsync("20261122150000_AddPlaybackCompletion");
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Users" ("Id", "Email", "NormalizedEmail", "PasswordHash", "Role", "Theme", "IsEnabled", "CreatedAt", "SecurityStamp")
            VALUES (1, 'one', 'ONE', 'test', 'Guest', 'Dark', 1, '2026-01-01', 'stamp');
            INSERT INTO "Playlists" ("Id", "UserId", "Name") VALUES (1, 1, 'Existing playlist');
            """);
        await database.Database.MigrateAsync();
        var playlist = await database.Playlists.SingleAsync();
        Assert.Equal("Existing playlist", playlist.Name);
        Assert.Equal(1, playlist.BackgroundId);
    }

    [Fact]
    public async Task PlaylistMigrationsApplyWhenLaterHistoryMigrationWasAlreadyApplied()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var database = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options);
        await database.Database.MigrateAsync();
        await database.Database.ExecuteSqlRawAsync("""
            DROP TABLE "PlaylistTracks";
            DROP TABLE "Playlists";
            ALTER TABLE "PlaybackStates" DROP COLUMN "RadioMode";
            DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" IN
              ('20261122142127_AddPlaylists', '20261122142204_AddRadioMode', '20261122160000_AddPlaylistBackground');
            """);
        await database.Database.MigrateAsync();
        Assert.Empty(await database.Playlists.ToListAsync());
        Assert.Empty(await database.PlaybackStates.ToListAsync());
    }

    [Fact]
    public async Task CompletionMigrationBackfillsOnlyPositionsAtTheKnownEnd()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
        await using var database = new MusicDbContext(options);
        await database.Database.GetService<IMigrator>().MigrateAsync("20261122140000_IndexPlaybackStatistics");
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Users" ("Id", "Email", "NormalizedEmail", "PasswordHash", "Role", "Theme", "IsEnabled", "CreatedAt", "SecurityStamp")
            VALUES (1, 'one', 'ONE', 'test', 'Guest', 'Dark', 1, '2026-01-01', 'stamp');
            INSERT INTO "SourceRoots" ("Id", "Name", "Path", "IsEnabled", "AddedAt")
            VALUES (1, 'Music', 'music', 1, '2026-01-01');
            INSERT INTO "Folders" ("Id", "SourceRootId", "Name", "RelativePath") VALUES (1, 1, 'Music', '');
            INSERT INTO "Tracks" ("Id", "SourceRootId", "FolderId", "RelativePath", "Title", "Artist", "Album",
                "AlbumArtist", "Genre", "TrackNumber", "DiscNumber", "Year", "DurationSeconds", "FileSize",
                "LastWriteTime", "ScannedAt", "IsAvailable")
            VALUES (1, 1, 1, 'one.mp3', 'One', '', '', '', '', 1, 1, 2020, 100, 1, '2026-01-01', '2026-01-01', 1);
            INSERT INTO "PlaybackHistory" ("UserId", "TrackId", "PlayId", "PositionSeconds", "StartedAtUnixMilliseconds", "UpdatedAtUnixMilliseconds")
            VALUES (1, 1, 'complete', 100, 1, 2), (1, 1, 'partial', 99, 3, 4), (1, 99, 'missing', 100, 5, 6);
            """);

        await database.Database.MigrateAsync();
        var entries = await database.PlaybackHistory.OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, entries[0].CompletedAtUnixMilliseconds);
        Assert.Null(entries[1].CompletedAtUnixMilliseconds);
        Assert.Null(entries[2].CompletedAtUnixMilliseconds);
    }

    [Fact]
    public async Task PlaybackStatisticsIndexMigrationPreservesHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
        await using var database = new MusicDbContext(options);
        await database.Database.GetService<IMigrator>().MigrateAsync("20261122100722_AddPlaybackState");
        database.Users.Add(new MusicUser { Id = 1, Email = "one", NormalizedEmail = "ONE", PasswordHash = "test" });
        await database.SaveChangesAsync();
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO "PlaybackHistory" ("UserId", "TrackId", "PlayId", "PositionSeconds", "StartedAtUnixMilliseconds", "UpdatedAtUnixMilliseconds")
            VALUES (1, 7, 'play', 12, 1, 2)
            """);

        await database.Database.MigrateAsync();

        Assert.Equal(12, (await database.PlaybackHistory.SingleAsync()).PositionSeconds);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master WHERE type = 'index'
                AND name = 'IX_PlaybackHistory_UserId_TrackId_StartedAtUnixMilliseconds_Id'
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ImportTimestampMigrationBackfillsPreviousScanTime()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>().UseSqlite(connection).Options;
        await using var database = new MusicDbContext(options);
        await database.Database.GetService<IMigrator>().MigrateAsync("20261031191400_RemoveAlbumDescription");
        await database.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "SourceRoots" ("Id", "Name", "Path", "IsEnabled", "AddedAt")
            VALUES (1, 'Music', 'C:\Music', 1, '2020-01-01 00:00:00+00:00');
            INSERT INTO "Folders" ("Id", "SourceRootId", "RelativePath", "Name")
            VALUES (1, 1, '', 'Music');
            INSERT INTO "Tracks" ("SourceRootId", "FolderId", "RelativePath", "Title", "Artist", "Album",
                "AlbumArtist", "Genre", "TrackNumber", "DiscNumber", "Year", "DurationSeconds", "FileSize",
                "LastWriteTime", "ScannedAt", "IsAvailable")
            VALUES (1, 1, 'track.wav', 'Track', 'Artist', 'Album', '', '', 1, 1, 2020, 1, 1,
                '2020-01-01 00:00:00+00:00', '2022-05-06 07:08:09+02:00', 1);
            """);

        await database.Database.MigrateAsync();

        var track = await database.Tracks.SingleAsync();
        Assert.Equal(new DateTimeOffset(2022, 5, 6, 5, 8, 9, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            track.ImportedAtUnixMilliseconds);
    }

    [Fact]
    public async Task MigrateCreatesTheCurrentSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MusicDbContext(options);

        await database.Database.MigrateAsync();

        Assert.True(await database.Database.CanConnectAsync());
        Assert.Equal(0, await database.Users.CountAsync());
        Assert.Contains(
            "20261024170421_InitialCreate",
            await database.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "20261031101714_AddProviderCredentials",
            await database.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "20261031191400_RemoveAlbumDescription",
            await database.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "20261115140000_AddImportTimestamp",
            await database.Database.GetAppliedMigrationsAsync());
        await AssertAlbumDescriptionColumnsRemovedAsync(database);
    }

    [Fact]
    public async Task ExistingEnsureCreatedDatabaseIsBaselinedWithoutLosingData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();
        await database.Database.ExecuteSqlRawAsync("DROP TABLE PlaylistTracks; DROP TABLE Playlists; DROP TABLE PlaybackHistory; DROP TABLE PlaybackStates; DROP TABLE TrackAudioAnalyses;");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE "Tracks" DROP COLUMN "PredictedGenre" """);
        await database.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Tracks" DROP COLUMN "ImportedAtUnixMilliseconds" """);
        await database.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "AlbumMetadata" ADD COLUMN "Description" TEXT NOT NULL DEFAULT ''""");
        await database.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "AlbumMetadata" ADD COLUMN "DescriptionEdited" INTEGER NOT NULL DEFAULT 0""");
        database.SourceRoots.Add(new MusicSourceRoot { Name = "Existing", Path = "C:\\Music" });
        database.AlbumMetadata.Add(new MusicAlbumMetadata
        {
            Key = "legacy-album",
            Artist = "Existing Artist",
            Album = "Existing Album",
            MusicBrainzId = "existing-mbid",
            ImageFileName = "cover.jpg",
            ImageRelativePath = "Existing Artist\\cover.jpg",
            ImageMissing = false,
        });
        await database.SaveChangesAsync();
        await database.Database.ExecuteSqlRawAsync("DROP TABLE ProviderCredentials");

        await DatabaseMigrationInitializer.MigrateAsync(database);

        Assert.Equal("Existing", (await database.SourceRoots.SingleAsync()).Name);
        var album = await database.AlbumMetadata.SingleAsync();
        Assert.Equal("existing-mbid", album.MusicBrainzId);
        Assert.Equal("cover.jpg", album.ImageFileName);
        Assert.Equal("Existing Artist\\cover.jpg", album.ImageRelativePath);
        Assert.False(album.ImageMissing);
        Assert.Contains(
            "20261024170421_InitialCreate",
            await database.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "20261031101714_AddProviderCredentials",
            await database.Database.GetAppliedMigrationsAsync());
        await AssertAlbumDescriptionColumnsRemovedAsync(database);
    }

    private static async Task AssertAlbumDescriptionColumnsRemovedAsync(MusicDbContext database)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = """PRAGMA table_info("AlbumMetadata")""";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.DoesNotContain("Description", columns);
        Assert.DoesNotContain("DescriptionEdited", columns);
    }
}
