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
        database.SourceRoots.Add(new MusicSourceRoot { Name = "Existing", Path = "C:\\Music" });
        await database.SaveChangesAsync();

        await DatabaseMigrationInitializer.MigrateAsync(database);

        Assert.Equal("Existing", (await database.SourceRoots.SingleAsync()).Name);
        Assert.Contains(
            "20261024170421_InitialCreate",
            await database.Database.GetAppliedMigrationsAsync());
    }
}
