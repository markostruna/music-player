using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class ProviderSettingsServiceTests
{
    [Fact]
    public async Task CredentialsAreEncryptedPersistedAndNeverReturned()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var protectionProvider = new EphemeralDataProtectionProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new ProviderSettingsService(
            database,
            protectionProvider,
            configuration);

        var settings = await service.UpdateAsync(
            "discogs",
            new Dictionary<string, string?> { ["token"] = "private-token" },
            CancellationToken.None);

        var storedCredential = await database.ProviderCredentials.SingleAsync();
        Assert.DoesNotContain("private-token", storedCredential.ProtectedValue, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(settings), StringComparison.Ordinal);
        Assert.True(settings.Single(provider => provider.Id == "discogs").IsConfigured);
        Assert.Equal("private-token", configuration["Discogs:Token"]);

        var restartedConfiguration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var restartedService = new ProviderSettingsService(database, protectionProvider, restartedConfiguration);
        await restartedService.LoadOverridesAsync(CancellationToken.None);
        Assert.Equal("private-token", restartedConfiguration["Discogs:Token"]);

        await service.UpdateAsync(
            "discogs",
            new Dictionary<string, string?> { ["token"] = "" },
            CancellationToken.None);

        Assert.False((await service.GetAsync(CancellationToken.None)).Single(provider => provider.Id == "discogs").IsConfigured);
        Assert.Equal("", configuration["Discogs:Token"]);
    }

    [Fact]
    public async Task DefaultAlbumProviderPersistsAndFallsBackWhenItsCredentialsAreRemoved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var protectionProvider = new EphemeralDataProtectionProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new ProviderSettingsService(database, protectionProvider, configuration);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateDefaultAlbumProviderAsync("discogs", CancellationToken.None));

        await service.UpdateAsync(
            "discogs",
            new Dictionary<string, string?> { ["token"] = "private-token" },
            CancellationToken.None);
        Assert.Equal(
            "discogs",
            await service.UpdateDefaultAlbumProviderAsync("discogs", CancellationToken.None));

        var restartedService = new ProviderSettingsService(database, protectionProvider, configuration);
        Assert.Equal("discogs", await restartedService.GetDefaultAlbumProviderAsync(CancellationToken.None));
        await restartedService.LoadOverridesAsync(CancellationToken.None);

        await service.UpdateAsync(
            "discogs",
            new Dictionary<string, string?> { ["token"] = "" },
            CancellationToken.None);

        Assert.Equal("musicbrainz", await service.GetDefaultAlbumProviderAsync(CancellationToken.None));
    }
}
