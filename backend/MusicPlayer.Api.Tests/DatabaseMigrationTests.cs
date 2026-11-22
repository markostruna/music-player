using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Tests;

public sealed class DatabaseMigrationTests
{
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
        await database.Database.ExecuteSqlRawAsync("DROP TABLE PlaybackHistory; DROP TABLE PlaybackStates;");
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
