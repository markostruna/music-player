using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Contracts;

/// <summary>Playlist name, ordered track IDs, and optional background selection.</summary>
public sealed record UpdatePlaylistRequest
{
    [Required, MaxLength(200)]
    public required string Name { get; init; }
    [Required]
    public required int[] TrackIds { get; init; }
    [Range(1, 20)]
    public int? BackgroundId { get; init; }
}

/// <summary>A personal playlist and its ordered tracks.</summary>
public sealed record PlaylistResponse(int Id, string Name, IReadOnlyList<TrackResponse> Tracks, int UnavailableTrackCount, int BackgroundId);
