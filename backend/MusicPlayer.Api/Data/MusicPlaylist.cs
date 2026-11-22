using System.ComponentModel.DataAnnotations;

namespace MusicPlayer.Api.Data;

public sealed class MusicPlaylist
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public MusicUser? User { get; set; }
    [MaxLength(200)]
    public required string Name { get; set; }
    public int BackgroundId { get; set; } = 1;
    public List<MusicPlaylistTrack> Tracks { get; set; } = [];
}

public sealed class MusicPlaylistTrack
{
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    public MusicPlaylist? Playlist { get; set; }
    public int TrackId { get; set; }
    public MusicTrack? Track { get; set; }
    public int Position { get; set; }
}
