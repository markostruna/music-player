using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public interface IAccountService
{
    Task<MusicUser?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken);
    Task BootstrapAdminAsync(string? email, string? password, CancellationToken cancellationToken);
    Task<IReadOnlyList<ManagedUserResponse>> GetUsersAsync(CancellationToken cancellationToken);
    Task<UserResponse?> GetUserAsync(int userId, CancellationToken cancellationToken);
    Task<ManagedUserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken);
    Task<bool> SetRoleAsync(int userId, UserRole role, CancellationToken cancellationToken);
    Task<bool> SetEnabledAsync(int userId, bool isEnabled, CancellationToken cancellationToken);
    Task<bool> ResetPasswordAsync(int userId, string newPassword, CancellationToken cancellationToken);
    Task<bool> SetThemeAsync(int userId, ThemeName theme, CancellationToken cancellationToken);
}
