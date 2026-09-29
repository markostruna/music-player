using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;
using MusicPlayer.Api.Services;

namespace MusicPlayer.Api.Tests;

public sealed class AccountServiceTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private MusicDbContext database = null!;
    private AccountService accounts = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MusicDbContext>()
            .UseSqlite(connection)
            .Options;
        database = new MusicDbContext(options);
        await database.Database.EnsureCreatedAsync();
        accounts = new AccountService(database, new PasswordHasher<MusicUser>());
    }

    public async Task DisposeAsync()
    {
        await database.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BootstrapAdminCreatesOnlyTheFirstAccount()
    {
        await accounts.BootstrapAdminAsync(" Admin@example.com ", "a-long-bootstrap-password", CancellationToken.None);
        await accounts.BootstrapAdminAsync("second@example.com", "another-long-password", CancellationToken.None);

        var admin = await database.Users.SingleAsync();
        Assert.Equal("Admin@example.com", admin.Email);
        Assert.Equal("ADMIN@EXAMPLE.COM", admin.NormalizedEmail);
        Assert.Equal(UserRole.Admin, admin.Role);
    }

    [Fact]
    public async Task AuthenticationIsCaseInsensitiveAndRejectsBadCredentials()
    {
        await accounts.BootstrapAdminAsync("admin@example.com", "a-long-bootstrap-password", CancellationToken.None);

        Assert.NotNull(await accounts.AuthenticateAsync(" ADMIN@example.com ", "a-long-bootstrap-password", CancellationToken.None));
        Assert.Null(await accounts.AuthenticateAsync("admin@example.com", "wrong-password", CancellationToken.None));
    }

    [Fact]
    public async Task LastActiveAdminCannotBeDemotedOrDisabled()
    {
        await accounts.BootstrapAdminAsync("admin@example.com", "a-long-bootstrap-password", CancellationToken.None);
        var admin = await database.Users.SingleAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.SetRoleAsync(admin.Id, UserRole.Guest, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.SetEnabledAsync(admin.Id, false, CancellationToken.None));

        var guest = await accounts.CreateUserAsync(new CreateUserRequest
        {
            Email = "guest@example.com",
            TemporaryPassword = "a-long-temporary-password",
            Role = UserRole.Guest,
        }, CancellationToken.None);
        Assert.True(await accounts.SetRoleAsync(guest.Id, UserRole.Admin, CancellationToken.None));
        Assert.True(await accounts.SetRoleAsync(admin.Id, UserRole.Guest, CancellationToken.None));
    }

    [Fact]
    public async Task ThemePreferenceIsStoredPerUser()
    {
        await accounts.BootstrapAdminAsync("admin@example.com", "a-long-bootstrap-password", CancellationToken.None);
        var guest = await accounts.CreateUserAsync(new CreateUserRequest
        {
            Email = "guest@example.com",
            TemporaryPassword = "a-long-temporary-password",
            Role = UserRole.Guest,
        }, CancellationToken.None);
        var admin = await database.Users.SingleAsync(user => user.Role == UserRole.Admin);

        Assert.True(await accounts.SetThemeAsync(guest.Id, ThemeName.Blue, CancellationToken.None));

        Assert.Equal(ThemeName.Blue, (await accounts.GetUserAsync(guest.Id, CancellationToken.None))?.Theme);
        Assert.Equal(ThemeName.Light, (await accounts.GetUserAsync(admin.Id, CancellationToken.None))?.Theme);
    }
}
