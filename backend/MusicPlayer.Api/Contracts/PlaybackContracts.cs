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

/// <summary>A genre found in tags, audio analysis, or both across the available library.</summary>
public sealed record GenreSourceResponse(string Name, bool FromTags, bool FromAnalysis);

/// <summary>Track and queue to use when selecting a similar track outside the queue.</summary>
public sealed record SimilarTrackRequest(int TrackId, int[] ExcludedTrackIds);

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
    IReadOnlyList<PlaybackDeviceResponse> Devices,
    string? RadioMode = null,
    bool SmartShuffle = false,
    TrackResponse? SuggestedTrack = null,
    int SmartQueueAdvances = 0);
