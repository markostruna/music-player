using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Data;

public sealed class MusicSourceRoot
{
    public int Id { get; set; }

    [MaxLength(120)]
    public required string Name { get; set; }

    [MaxLength(2048)]
    public required string Path { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<MusicFolder> Folders { get; } = new List<MusicFolder>();
    public ICollection<MusicTrack> Tracks { get; } = new List<MusicTrack>();
}
