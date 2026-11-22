using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Contracts;

/// <summary>Playlist name and ordered track IDs.</summary>
public sealed record UpdatePlaylistRequest
{
    [Required, MaxLength(200)]
    public required string Name { get; init; }
    [Required]
    public required int[] TrackIds { get; init; }
}

/// <summary>A personal playlist and its ordered tracks.</summary>
public sealed record PlaylistResponse(int Id, string Name, IReadOnlyList<TrackResponse> Tracks, int UnavailableTrackCount);
