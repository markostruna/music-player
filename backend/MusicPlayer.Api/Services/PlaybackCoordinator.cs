using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Data;

namespace MusicPlayer.Api.Services;

public sealed class PlaybackCoordinator(TimeProvider timeProvider)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<int, AccountDevices> accounts = new();

    private sealed class Device(string name, DateTimeOffset seen)
    {
        public string Name { get; set; } = name;
        public DateTimeOffset Seen { get; set; } = seen;
    }

    private sealed class AccountDevices
    {
        public Dictionary<string, Device> Devices { get; } = new();
        public string? Owner { get; set; }
        public string? Target { get; set; }
        public long PositionRevision { get; set; }
        public Dictionary<string, int> IssuedPlays { get; } = new();

        public AccountDevices Copy()
        {
            var copy = new AccountDevices { Owner = Owner, Target = Target, PositionRevision = PositionRevision };
            foreach (var (id, device) in Devices)
                copy.Devices.Add(id, new Device(device.Name, device.Seen));
            foreach (var (id, trackId) in IssuedPlays)
                copy.IssuedPlays.Add(id, trackId);
            return copy;
        }
    }

    public async Task<PlaybackResponse> PollAsync(
        int userId, PlaybackPollRequest request, MusicDbContext database,
        MusicLibraryBrowseService library, CancellationToken cancellationToken)
    {
        if (!ValidSession(request.SessionId) || string.IsNullOrWhiteSpace(request.DeviceName)
            || request.DeviceName.Length > 120 || !ValidPosition(request.PositionSeconds))
            throw new ArgumentException("Invalid playback session.");

        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var devices = GetDevices(userId, now).Copy();
            devices.Devices[request.SessionId] = new Device(request.DeviceName, now);
            var state = await GetStateAsync(userId, database, cancellationToken);
            await RemoveUnavailableTracksAsync(state, library, cancellationToken);
            RememberPlay(devices, state);

            if (devices.Owner == request.SessionId && request.IsPlaying
                && request.PlayId is not null && devices.IssuedPlays.TryGetValue(request.PlayId, out var playedTrack))
                await UpdateHistoryEntryAsync(userId, playedTrack, request.PlayId,
                    request.PositionSeconds, database, now, true, cancellationToken);

            if (devices.Owner == request.SessionId && devices.Target is not null && request.TransferStopped)
            {
                if (request.Revision == state.Revision)
                {
                    state.PositionSeconds = request.PositionSeconds;
                    await UpdateHistoryAsync(state, database, now, false, cancellationToken);
                }
                devices.Owner = devices.Target;
                devices.Target = null;
                state.Revision++;
            }
            if (devices.Owner is null)
            {
                devices.Owner = request.SessionId;
                state.Revision++;
            }

            if (devices.Owner == request.SessionId && devices.Target is null
                && request.Revision == state.Revision)
            {
                state.PositionSeconds = request.PositionSeconds;
                await UpdateHistoryAsync(state, database, now, request.IsPlaying, cancellationToken);
            }
            await database.SaveChangesAsync(cancellationToken);
            accounts[userId] = devices;
            return await MakeResponseAsync(state, devices, request.Revision, library, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlaybackResponse> CommandAsync(
        int userId, PlaybackCommandRequest request, MusicDbContext database,
        MusicLibraryBrowseService library, CancellationToken cancellationToken)
    {
        if (!ValidSession(request.SessionId)) throw new ArgumentException("Invalid playback session.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var devices = GetDevices(userId, timeProvider.GetUtcNow()).Copy();
            if (!devices.Devices.ContainsKey(request.SessionId))
                throw new PlaybackConflictException("Playback session has expired. Reconnect and try again.");
            var state = await GetStateAsync(userId, database, cancellationToken);
            if (request.Revision != state.Revision)
                throw new PlaybackConflictException("Playback changed on another device. Try again.");
            if (request.Action is "ended" or "failed" && request.ExpectedPlayId != state.PlayId)
                throw new PlaybackConflictException("The playback event belongs to an earlier track.");
            if (request.PositionSeconds is double suppliedPosition && !ValidPosition(suppliedPosition))
                throw new ArgumentException("Invalid playback position.");
            if (request.StartPositionSeconds is double startPosition
                && (request.Action != "play" || !ValidPosition(startPosition)))
                throw new ArgumentException("Invalid starting position.");
            if (devices.Owner == request.SessionId && request.Action != "seek"
                && request.PositionSeconds is double position && ValidPosition(position))
            {
                state.PositionSeconds = position;
                await UpdateHistoryAsync(state, database, timeProvider.GetUtcNow(),
                    (request.WasPlaying || request.Action == "ended") && state.IsPlaying, cancellationToken);
            }
            var queue = ReadQueue(state);
            var startTrack = false;
            switch (request.Action)
            {
                case "radio-album":
                case "radio-track":
                case "random-albums":
                case "random-tracks":
                    var randomTracks = await library.GetRandomTracksAsync(
                        request.Action is "radio-album" or "random-albums",
                        request.Action switch { "random-albums" => 10, "random-tracks" => 100, _ => 1 },
                        cancellationToken);
                    if (randomTracks.Count == 0) throw new ArgumentException("There are no playable tracks in the library.");
                    queue = randomTracks.Select(track => track.Id).ToArray();
                    state.QueueJson = JsonSerializer.Serialize(queue);
                    state.CurrentIndex = 0;
                    state.RadioMode = request.Action is "radio-album" or "radio-track" ? request.Action : null;
                    state.Shuffle = false;
                    state.SmartShuffle = false;
                    state.SmartQueueAdvances = 0;
                    state.SuggestedTrackId = null;
                    state.Repeat = false;
                    startTrack = true;
                    break;
                case "play":
                case "enqueue":
                    if (request.TrackIds is null || request.TrackIds.Length == 0
                        || request.TrackIds.Any(id => id <= 0))
                        throw new ArgumentException("Invalid playback queue.");
                    var tracks = await library.GetTracksByIdsAsync(request.TrackIds, cancellationToken);
                    if (tracks.Count != request.TrackIds.Length)
                        throw new ArgumentException("The queue contains unavailable tracks.");
                    if (request.Action == "play" || queue.Length == 0)
                    {
                        if (request.Index is null || request.Index < 0 || request.Index >= request.TrackIds.Length)
                            throw new ArgumentException("Invalid track index.");
                        if (request.StartPositionSeconds is double start
                            && (tracks[request.Index.Value].DurationSeconds <= 0
                                || start >= tracks[request.Index.Value].DurationSeconds))
                            throw new ArgumentException("Invalid starting position.");
                        queue = request.TrackIds;
                        state.CurrentIndex = request.Index.Value;
                        state.SuggestedTrackId = null;
                        state.SmartQueueAdvances = 0;
                        startTrack = true;
                    }
                    else
                    {
                        queue = queue.Concat(request.TrackIds).ToArray();
                    }
                    state.QueueJson = JsonSerializer.Serialize(queue);
                    state.RadioMode = null;
                    break;
                case "index":
                    if (request.Index is null || request.Index < 0 || request.Index >= queue.Length)
                        throw new ArgumentException("Invalid track index.");
                    state.CurrentIndex = request.Index.Value;
                    state.SuggestedTrackId = null;
                    state.SmartQueueAdvances = 0;
                    startTrack = true;
                    break;
                case "next":
                case "ended":
                    if (request.Action == "ended" && devices.Owner != request.SessionId)
                        throw new PlaybackConflictException("Only the playing device can advance a finished track.");
                    if (request.Action == "ended" && state.IsPlaying)
                        await UpdateHistoryAsync(state, database, timeProvider.GetUtcNow(), true,
                            cancellationToken, completed: true);
                    if (request.Action == "ended" && state.Repeat && state.CurrentIndex >= 0)
                    {
                        startTrack = true;
                        break;
                    }
                    var wasSuggestion = state.SuggestedTrackId is not null;
                    var previousTrackId = state.SuggestedTrackId
                        ?? (state.CurrentIndex >= 0 && state.CurrentIndex < queue.Length
                            ? queue[state.CurrentIndex] : (int?)null);
                    if (wasSuggestion)
                    {
                        state.SuggestedTrackId = null;
                    }
                    else if (state.SmartShuffle && state.CurrentIndex >= 0 && state.CurrentIndex < queue.Length)
                    {
                        state.SmartQueueAdvances++;
                        if (state.SmartQueueAdvances >= 3)
                        {
                            state.SmartQueueAdvances = 0;
                            var suggestion = await library.FindSimilarTrackAsync(
                                queue[state.CurrentIndex], queue, cancellationToken);
                            if (suggestion is not null)
                            {
                                state.SuggestedTrackId = suggestion.Id;
                                startTrack = true;
                                break;
                            }
                        }
                    }
                    if (state.Shuffle && queue.Length > 0)
                    {
                        var next = queue.Length == 1 ? 0 : Random.Shared.Next(queue.Length - 1);
                        state.CurrentIndex = next >= state.CurrentIndex && queue.Length > 1 ? next + 1 : next;
                        startTrack = true;
                    }
                    else if (state.CurrentIndex >= 0 && state.CurrentIndex < queue.Length - 1)
                    {
                        state.CurrentIndex++;
                        startTrack = true;
                    }
                    else if (state.RadioMode is "radio-album" or "radio-track")
                    {
                        var nextTracks = await library.GetRandomTracksAsync(state.RadioMode == "radio-album", 1, cancellationToken);
                        if (nextTracks.Count == 0)
                        {
                            state.IsPlaying = false;
                            state.RadioMode = null;
                        }
                        else
                        {
                            queue = nextTracks.Select(track => track.Id).ToArray();
                            state.QueueJson = JsonSerializer.Serialize(queue);
                            state.CurrentIndex = 0;
                            startTrack = true;
                        }
                    }
                    else if (previousTrackId is int finishedTrackId)
                    {
                        var suggestion = await library.FindSimilarTrackAsync(
                            finishedTrackId, [], cancellationToken);
                        if (suggestion is not null)
                        {
                            state.SuggestedTrackId = suggestion.Id;
                            startTrack = true;
                        }
                        else
                        {
                            state.SuggestedTrackId = wasSuggestion ? finishedTrackId : null;
                            state.IsPlaying = false;
                        }
                    }
                    break;
                case "previous":
                    if (state.PositionSeconds > 3)
                    {
                        state.PositionSeconds = 0;
                        devices.PositionRevision = state.Revision + 1;
                    }
                    else if (state.SuggestedTrackId is not null)
                    {
                        state.SuggestedTrackId = null;
                        state.SmartQueueAdvances = 0;
                        startTrack = true;
                    }
                    else if (state.CurrentIndex <= 0)
                    {
                        state.PositionSeconds = 0;
                        devices.PositionRevision = state.Revision + 1;
                    }
                    else
                    {
                        state.CurrentIndex--;
                        state.SmartQueueAdvances = 0;
                        startTrack = true;
                    }
                    break;
                case "toggle":
                    if (state.CurrentIndex < 0 || state.CurrentIndex >= queue.Length)
                        throw new ArgumentException("There is no current track.");
                    state.IsPlaying = !state.IsPlaying;
                    if (state.IsPlaying && state.PlayId.Length == 0)
                        state.PlayId = Guid.NewGuid().ToString("N");
                    break;
                case "seek":
                    if (request.PositionSeconds is null || !ValidPosition(request.PositionSeconds.Value))
                        throw new ArgumentException("Invalid playback position.");
                    state.PositionSeconds = request.PositionSeconds.Value;
                    devices.PositionRevision = state.Revision + 1;
                    await UpdateHistoryAsync(state, database, timeProvider.GetUtcNow(), false, cancellationToken);
                    break;
                case "shuffle":
                    state.Shuffle = !state.Shuffle;
                    state.SmartShuffle = false;
                    state.SmartQueueAdvances = 0;
                    break;
                case "mode-normal":
                case "mode-shuffle":
                case "mode-smart":
                    state.Shuffle = request.Action == "mode-shuffle";
                    state.SmartShuffle = request.Action == "mode-smart";
                    state.SmartQueueAdvances = 0;
                    break;
                case "repeat": state.Repeat = !state.Repeat; break;
                case "transfer":
                    if (request.TargetSessionId is null || !devices.Devices.ContainsKey(request.TargetSessionId))
                        throw new ArgumentException("The selected device is unavailable.");
                    if (request.StartPlaying && state.CurrentIndex >= 0 && state.CurrentIndex < queue.Length)
                    {
                        state.IsPlaying = true;
                        if (state.PlayId.Length == 0) state.PlayId = Guid.NewGuid().ToString("N");
                    }
                    if (devices.Owner != request.TargetSessionId)
                    {
                        if (devices.Owner is null) devices.Owner = request.TargetSessionId;
                        else devices.Target = request.TargetSessionId;
                    }
                    break;
                case "failed":
                    if (devices.Owner != request.SessionId)
                        throw new PlaybackConflictException("Only the playing device can report playback failure.");
                    state.IsPlaying = false;
                    break;
                case "leave":
                    devices.Devices.Remove(request.SessionId);
                    if (devices.Target == request.SessionId) devices.Target = null;
                    if (devices.Owner == request.SessionId)
                    {
                        devices.Owner = null;
                        devices.Target = null;
                    }
                    break;
                default: throw new ArgumentException("Unknown playback action.");
            }
            if (startTrack)
            {
                state.PositionSeconds = request.Action == "play" ? request.StartPositionSeconds ?? 0 : 0;
                state.IsPlaying = true;
                state.PlayId = Guid.NewGuid().ToString("N");
                devices.PositionRevision = state.Revision + 1;
            }
            RememberPlay(devices, state);
            state.Revision++;
            await database.SaveChangesAsync(cancellationToken);
            accounts[userId] = devices;
            return await MakeResponseAsync(state, devices, request.Revision, library, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private AccountDevices GetDevices(int userId, DateTimeOffset now)
    {
        foreach (var stale in accounts.Where(account => account.Key != userId
                     && account.Value.Devices.Values.All(device => now - device.Seen > LeaseDuration))
                     .Select(account => account.Key).ToArray())
            accounts.Remove(stale);
        if (!accounts.TryGetValue(userId, out var devices))
        {
            devices = new AccountDevices();
            accounts.Add(userId, devices);
        }
        foreach (var id in devices.Devices.Where(item => now - item.Value.Seen > LeaseDuration)
                     .Select(item => item.Key).ToArray())
            devices.Devices.Remove(id);
        if (devices.Owner is not null && !devices.Devices.ContainsKey(devices.Owner))
        {
            devices.Owner = null;
            if (devices.Target is not null && devices.Devices.ContainsKey(devices.Target))
                devices.Owner = devices.Target;
            devices.Target = null;
        }
        if (devices.Target is not null && !devices.Devices.ContainsKey(devices.Target))
            devices.Target = null;
        return devices;
    }

    private static async Task<PlaybackState> GetStateAsync(
        int userId, MusicDbContext database, CancellationToken cancellationToken)
    {
        var state = await database.PlaybackStates.FindAsync([userId], cancellationToken);
        if (state is not null) return state;
        state = new PlaybackState { UserId = userId };
        database.PlaybackStates.Add(state);
        return state;
    }

    private static async Task UpdateHistoryAsync(
        PlaybackState state, MusicDbContext database, DateTimeOffset now, bool create,
        CancellationToken cancellationToken, bool completed = false)
    {
        var queue = ReadQueue(state);
        if (state.CurrentIndex < 0 || state.CurrentIndex >= queue.Length || state.PlayId.Length == 0) return;
        await UpdateHistoryEntryAsync(state.UserId, state.SuggestedTrackId ?? queue[state.CurrentIndex], state.PlayId,
            state.PositionSeconds, database, now, create, cancellationToken, completed);
    }

    private static async Task UpdateHistoryEntryAsync(
        int userId, int trackId, string playId, double position, MusicDbContext database,
        DateTimeOffset now, bool create, CancellationToken cancellationToken, bool completed = false)
    {
        var history = database.PlaybackHistory.Local.FirstOrDefault(item => item.UserId == userId && item.PlayId == playId)
            ?? await database.PlaybackHistory.SingleOrDefaultAsync(
                item => item.UserId == userId && item.PlayId == playId, cancellationToken);
        if (history is null)
        {
            if (!create) return;
            history = new PlaybackHistory
            {
                UserId = userId,
                TrackId = trackId,
                PlayId = playId,
                StartedAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            };
            database.PlaybackHistory.Add(history);
        }
        history.PositionSeconds = position;
        history.UpdatedAtUnixMilliseconds = now.ToUnixTimeMilliseconds();
        if (completed) history.CompletedAtUnixMilliseconds ??= now.ToUnixTimeMilliseconds();
    }

    private static void RememberPlay(AccountDevices devices, PlaybackState state)
    {
        var queue = ReadQueue(state);
        if (state.PlayId.Length == 0 || state.CurrentIndex < 0 || state.CurrentIndex >= queue.Length) return;
        devices.IssuedPlays.TryAdd(state.PlayId, state.SuggestedTrackId ?? queue[state.CurrentIndex]);
        if (devices.IssuedPlays.Count > 100)
            devices.IssuedPlays.Remove(devices.IssuedPlays.Keys.First());
    }

    private static async Task RemoveUnavailableTracksAsync(
        PlaybackState state, MusicLibraryBrowseService library, CancellationToken cancellationToken)
    {
        var ids = ReadQueue(state);
        if (ids.Length == 0) return;
        var available = await library.GetAvailableTrackIdsAsync(
            state.SuggestedTrackId is int suggested ? ids.Append(suggested).ToArray() : ids, cancellationToken);
        if (state.SuggestedTrackId is int suggestion && !available.Contains(suggestion))
        {
            state.SuggestedTrackId = null;
            state.IsPlaying = false;
            state.PositionSeconds = 0;
            state.PlayId = "";
            state.Revision++;
        }
        if (ids.All(available.Contains)) return;
        var currentAvailable = state.CurrentIndex >= 0 && state.CurrentIndex < ids.Length
            && available.Contains(ids[state.CurrentIndex]);
        var index = state.CurrentIndex < 0 ? -1
            : ids.Take(Math.Min(state.CurrentIndex, ids.Length)).Count(available.Contains);
        var remaining = ids.Where(available.Contains).ToArray();
        state.QueueJson = JsonSerializer.Serialize(remaining);
        state.CurrentIndex = currentAvailable ? index
            : remaining.Length == 0 ? -1 : Math.Clamp(index, 0, remaining.Length - 1);
        if (!currentAvailable)
        {
            state.SuggestedTrackId = null;
            state.IsPlaying = false;
            state.PositionSeconds = 0;
            state.PlayId = "";
        }
        state.Revision++;
    }

    private static async Task<PlaybackResponse> MakeResponseAsync(
        PlaybackState state, AccountDevices devices, long knownRevision, MusicLibraryBrowseService library,
        CancellationToken cancellationToken)
    {
        var queue = state.Revision == knownRevision
            ? null
            : await library.GetTracksByIdsAsync(ReadQueue(state), cancellationToken);
        var suggestion = state.SuggestedTrackId is int suggestionId
            ? (await library.GetTracksByIdsAsync([suggestionId], cancellationToken)).SingleOrDefault()
            : null;
        return new PlaybackResponse(state.Revision, devices.PositionRevision, state.PlayId,
            queue, state.CurrentIndex, state.PositionSeconds,
            state.IsPlaying, state.Shuffle, state.Repeat, devices.Owner, devices.Target,
            devices.Devices.Select(item => new PlaybackDeviceResponse(item.Key, item.Value.Name)).ToArray(),
            state.RadioMode, state.SmartShuffle, suggestion, state.SmartQueueAdvances);
    }

    private static bool ValidSession(string? value) => Guid.TryParse(value, out _);
    private static bool ValidPosition(double value) => double.IsFinite(value) && value >= 0;
    private static int[] ReadQueue(PlaybackState state) =>
        JsonSerializer.Deserialize<int[]>(state.QueueJson)
            ?? throw new InvalidDataException("The saved playback queue is invalid.");
}

public sealed class PlaybackConflictException(string message) : Exception(message);
