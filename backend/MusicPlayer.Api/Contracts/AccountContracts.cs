using System.ComponentModel.DataAnnotations;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Contracts;

/// <summary>Credentials for signing into the music library.</summary>
public sealed record LoginRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public required string Email { get; init; }

    [Required, MinLength(1), MaxLength(256)]
    public required string Password { get; init; }
}

/// <summary>Identity and personal settings for the signed-in account.</summary>
public sealed record UserResponse(int Id, string Email, UserRole Role, ThemeName Theme);

/// <summary>Information about an account the administrator creates.</summary>
public sealed record CreateUserRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public required string Email { get; init; }

    [Required, MinLength(12), MaxLength(256)]
    public required string TemporaryPassword { get; init; }

    [Required, EnumDataType(typeof(UserRole))]
    public required UserRole Role { get; init; }
}

/// <summary>Role to assign to an existing account.</summary>
public sealed record SetUserRoleRequest
{
    [Required, EnumDataType(typeof(UserRole))]
    public required UserRole Role { get; init; }
}

/// <summary>Whether an account may sign in.</summary>
public sealed record SetUserEnabledRequest
{
    public required bool IsEnabled { get; init; }
}

/// <summary>A replacement password supplied by an administrator.</summary>
public sealed record ResetPasswordRequest
{
    [Required, MinLength(12), MaxLength(256)]
    public required string NewPassword { get; init; }
}

/// <summary>The signed-in user's selected interface theme.</summary>
public sealed record SetThemeRequest
{
    [Required, EnumDataType(typeof(ThemeName))]
    public required ThemeName Theme { get; init; }
}

/// <summary>A response item in the administrator's user list.</summary>
public sealed record ManagedUserResponse(int Id, string Email, UserRole Role, bool IsEnabled, DateTimeOffset CreatedAt);
