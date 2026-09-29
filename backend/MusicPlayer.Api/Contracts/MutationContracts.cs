using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Contracts;

/// <summary>Optional embedded tag fields to update; null fields are left unchanged.</summary>
public sealed record MusicMetadataPatch
{
    [MaxLength(1024)]
    public string? Title { get; init; }

    [MaxLength(1024)]
    public string? Artist { get; init; }

    [MaxLength(1024)]
    public string? Album { get; init; }

    [MaxLength(1024)]
    public string? AlbumArtist { get; init; }

    [MaxLength(256)]
    public string? Genre { get; init; }

    public uint? TrackNumber { get; init; }
    public uint? DiscNumber { get; init; }
    public uint? Year { get; init; }
}

/// <summary>A metadata update applied to the listed track IDs.</summary>
public sealed record BatchMetadataRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public required int[] TrackIds { get; init; }

    [Required]
    public required MusicMetadataPatch Changes { get; init; }
}

/// <summary>Embedded image bytes applied as front cover art to listed tracks.</summary>
public sealed record BatchCoverRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public required int[] TrackIds { get; init; }

    [Required, MinLength(8), MaxLength(10485760)]
    public required byte[] Image { get; init; }
}

/// <summary>Destination folder for selected catalog tracks.</summary>
public sealed record MoveTracksRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public required int[] TrackIds { get; init; }

    [Range(1, int.MaxValue)]
    public required int DestinationFolderId { get; init; }
}

/// <summary>Outcome for one track in a metadata or move operation.</summary>
public sealed record TrackMutationResult(int TrackId, bool Succeeded, string Message);

/// <summary>Per-track outcomes for a batch metadata or move operation.</summary>
public sealed record BatchMutationResponse(IReadOnlyList<TrackMutationResult> Results);
