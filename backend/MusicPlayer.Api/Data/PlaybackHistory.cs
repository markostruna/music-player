namespace MusicPlayer.Api.Data;

public sealed class PlaybackHistory
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public MusicUser? User { get; set; }
    public int TrackId { get; set; }
    public string PlayId { get; set; } = "";
    public double PositionSeconds { get; set; }
    public long StartedAtUnixMilliseconds { get; set; }
    public long UpdatedAtUnixMilliseconds { get; set; }
}
