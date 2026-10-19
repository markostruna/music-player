using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Data;

public sealed class MusicAlbumMetadata
{
    [MaxLength(64)]
    public required string Key { get; set; }

    [MaxLength(1024)]
    public required string Artist { get; set; }

    [MaxLength(1024)]
    public required string Album { get; set; }

    [MaxLength(20000)]
    public string Description { get; set; } = "";

    public bool DescriptionEdited { get; set; }

    [MaxLength(64)]
    public string MusicBrainzId { get; set; } = "";

    [MaxLength(128)]
    public string? ImageFileName { get; set; }

    public int? ImageSourceRootId { get; set; }

    [MaxLength(2048)]
    public string? ImageRelativePath { get; set; }

    public bool ImageMissing { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
