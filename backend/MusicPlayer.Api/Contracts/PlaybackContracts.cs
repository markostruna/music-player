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
    string? ExpectedPlayId = null);

public sealed record PlaybackDeviceResponse(string Id, string Name);

public sealed record PlaybackHistoryResponse(
    int TrackId, double PositionSeconds, long StartedAtUnixMilliseconds, long UpdatedAtUnixMilliseconds);

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
