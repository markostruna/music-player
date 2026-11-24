namespace MusicPlayer.Api.Data;

public sealed class TrackAudioAnalysis
{
    public int TrackId { get; set; }
    public MusicTrack? Track { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
    public int Version { get; set; }
    public required string Genre { get; set; }
    public required string EmbeddingJson { get; set; }
}
