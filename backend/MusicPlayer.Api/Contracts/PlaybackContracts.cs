namespace MusicPlayer.Api.Contracts;

public sealed record PlaybackPollRequest(
    string SessionId,
    string DeviceName,
    long Revision,
    double PositionSeconds,
    bool IsPlaying,
    bool TransferStopped,
    string PlayId = "");

public sealed record PlaybackCommandRequest(
    string SessionId,
    long Revision,
    string Action,
    int[]? TrackIds,
    int? Index,
    double? PositionSeconds,
    string? TargetSessionId,
    bool WasPlaying = false,
    bool StartPlaying = false,
    string? ExpectedPlayId = null,
    double? StartPositionSeconds = null);

public sealed record PlaybackDeviceResponse(string Id, string Name);

public sealed record PlaybackHistoryResponse(
    int TrackId, double PositionSeconds, long StartedAtUnixMilliseconds, long UpdatedAtUnixMilliseconds);

/// <summary>Playback statistics and the latest saved position for an available track.</summary>
public sealed record PlayedTrackResponse(
    TrackResponse Track, int PlayCount, double PositionSeconds, DateTimeOffset LastPlayedAt);

/// <summary>Recently played and partially played tracks for the signed-in account.</summary>
public sealed record PlaybackStatisticsResponse(
    IReadOnlyList<PlayedTrackResponse> RecentlyPlayed,
    IReadOnlyList<PlayedTrackResponse> ContinueListening);

public sealed record PlaybackResponse(
    long Revision,
    long PositionRevision,
    string PlayId,
    IReadOnlyList<TrackResponse>? Queue,
    int CurrentIndex,
    double PositionSeconds,
    bool IsPlaying,
    bool Shuffle,
    bool Repeat,
    string? OwnerSessionId,
    string? TransferTargetSessionId,
    IReadOnlyList<PlaybackDeviceResponse> Devices);
