using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Data;

public enum UserRole
{
    Guest,
    Admin,
}

public enum ThemeName
{
    Light,
    Dark,
    Blue,
}

public sealed class MusicUser
{
    public int Id { get; set; }

    [MaxLength(320)]
    public required string Email { get; set; }

    [MaxLength(320)]
    public required string NormalizedEmail { get; set; }

    public required string PasswordHash { get; set; }

    [MaxLength(64)]
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public UserRole Role { get; set; } = UserRole.Guest;
    public ThemeName Theme { get; set; } = ThemeName.Light;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
