namespace MusicPlayer.Api.Data;

public sealed class PlaybackState
{
    public int UserId { get; set; }
    public MusicUser? User { get; set; }
    public string QueueJson { get; set; } = "[]";
    public int CurrentIndex { get; set; } = -1;
    public double PositionSeconds { get; set; }
    public bool IsPlaying { get; set; }
    public bool Shuffle { get; set; }
    public bool Repeat { get; set; }
    public long Revision { get; set; }
    public string PlayId { get; set; } = "";
    public string? RadioMode { get; set; }
}
