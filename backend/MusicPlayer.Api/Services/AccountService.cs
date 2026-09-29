using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public sealed class AccountService(MusicDbContext database, IPasswordHasher<MusicUser> passwordHasher) : IAccountService
{
    public async Task<MusicUser?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var user = await database.Users.SingleOrDefaultAsync(
            account => account.NormalizedEmail == normalizedEmail && account.IsEnabled,
            cancellationToken);
        if (user is null)
        {
            return null;
        }

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            await database.SaveChangesAsync(cancellationToken);
        }

        return user;
    }

    public async Task BootstrapAdminAsync(string? email, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (password.Length < 12 || !new EmailAddressAttribute().IsValid(email))
        {
            throw new InvalidOperationException("The bootstrap administrator credentials are invalid.");
        }

        if (await database.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var admin = new MusicUser
        {
            Email = email.Trim(),
            NormalizedEmail = NormalizeEmail(email),
            PasswordHash = string.Empty,
            Role = UserRole.Admin,
        };
        admin.PasswordHash = passwordHasher.HashPassword(admin, password);
        database.Users.Add(admin);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedUserResponse>> GetUsersAsync(CancellationToken cancellationToken) =>
        await database.Users.AsNoTracking()
            .OrderBy(user => user.Email)
            .Select(user => new ManagedUserResponse(user.Id, user.Email, user.Role, user.IsEnabled, user.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<UserResponse?> GetUserAsync(int userId, CancellationToken cancellationToken) =>
        await database.Users.AsNoTracking()
            .Where(user => user.Id == userId && user.IsEnabled)
            .Select(user => new UserResponse(user.Id, user.Email, user.Role, user.Theme))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ManagedUserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Role))
        {
            throw new ArgumentException("The requested role is invalid.");
        }

        var email = request.Email.Trim();
        var normalizedEmail = NormalizeEmail(email);
        if (await database.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            throw new InvalidOperationException("An account with this email already exists.");
        }

        var user = new MusicUser
        {
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = string.Empty,
            Role = request.Role,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.TemporaryPassword);
        database.Users.Add(user);
        await database.SaveChangesAsync(cancellationToken);
        return ToManagedResponse(user);
    }

    public async Task<bool> SetRoleAsync(int userId, UserRole role, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentException("The requested role is invalid.");
        }

        var user = await database.Users.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            return false;
        }

        if (user.Role == UserRole.Admin && role != UserRole.Admin && user.IsEnabled && await IsLastActiveAdminAsync(userId, cancellationToken))
        {
            throw new InvalidOperationException("The last active administrator cannot be demoted.");
        }

        user.Role = role;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetEnabledAsync(int userId, bool isEnabled, CancellationToken cancellationToken)
    {
        var user = await database.Users.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            return false;
        }

        if (!isEnabled && user.Role == UserRole.Admin && user.IsEnabled && await IsLastActiveAdminAsync(userId, cancellationToken))
        {
            throw new InvalidOperationException("The last active administrator cannot be disabled.");
        }

        user.IsEnabled = isEnabled;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ResetPasswordAsync(int userId, string newPassword, CancellationToken cancellationToken)
    {
        var user = await database.Users.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetThemeAsync(int userId, ThemeName theme, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentException("The requested theme is invalid.");
        }

        var user = await database.Users.FindAsync([userId], cancellationToken);
        if (user is null || !user.IsEnabled)
        {
            return false;
        }

        user.Theme = theme;
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> IsLastActiveAdminAsync(int userId, CancellationToken cancellationToken) =>
        await database.Users.CountAsync(
            user => user.Id != userId && user.Role == UserRole.Admin && user.IsEnabled,
            cancellationToken) == 0;

    private static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    private static ManagedUserResponse ToManagedResponse(MusicUser user) =>
        new(user.Id, user.Email, user.Role, user.IsEnabled, user.CreatedAt);
}
