using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Tests;

public sealed class DatabaseMigrationTests
{
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
