using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Data;

public sealed class MusicTrack
{
    public int Id { get; set; }
    public int SourceRootId { get; set; }
    public MusicSourceRoot? SourceRoot { get; set; }
    public int FolderId { get; set; }
    public MusicFolder? Folder { get; set; }

    [MaxLength(2048)]
    public required string RelativePath { get; set; }

    [MaxLength(1024)]
    public required string Title { get; set; }

    [MaxLength(1024)]
    public string Artist { get; set; } = "Unknown artist";

    [MaxLength(1024)]
    public string Album { get; set; } = "Unknown album";

    [MaxLength(1024)]
    public string AlbumArtist { get; set; } = "";

    [MaxLength(256)]
    public string Genre { get; set; } = "";

    public uint TrackNumber { get; set; }
    public uint DiscNumber { get; set; }
    public uint Year { get; set; }
    public double DurationSeconds { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
    public bool IsAvailable { get; set; } = true;
}
