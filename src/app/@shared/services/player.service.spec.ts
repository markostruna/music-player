import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthState } from './auth-state';
import { MusicLibraryApi, MusicTrackResponse } from './music-library-api';
import { PlaybackCommand, PlaybackSnapshot, PlaybackSyncApi } from './playback-sync-api';
import { PlayerService } from './player.service';

class FakeAudio {
  static instances: FakeAudio[] = [];
  private readonly listeners = new Map<string, (() => void)[]>();
  preload = '';
  volume = 1;
  paused = true;
  currentTime = 0;
  duration = 180;
  readyState = 1;
  src = '';
  play = vi.fn(() => {
    this.paused = false;
    return Promise.resolve();
  });
  pause = vi.fn(() => {
    this.paused = true;
  });
  load = vi.fn();
  addEventListener = vi.fn((name: string, listener: () => void) => {
    this.listeners.set(name, [...(this.listeners.get(name) ?? []), listener]);
  });
  removeAttribute = vi.fn();

  emit(name: string): void {
    for (const listener of this.listeners.get(name) ?? []) listener();
  }

  constructor() {
    FakeAudio.instances.push(this);
  }
}

const track: MusicTrackResponse = {
  id: 1,
  sourceRootId: 1,
  folderId: 1,
  relativePath: 'one.mp3',
  title: 'One',
  artist: 'Artist',
  album: 'Album',
  albumArtist: 'Artist',
  genre: '',
  trackNumber: 1,
  discNumber: 1,
  year: 2020,
  durationSeconds: 180,
  streamUrl: '/one',
  coverUrl: '/cover',
  fileExtension: 'mp3',
};

function snapshot(owner: string | null, revision = 1): PlaybackSnapshot {
  return {
    revision,
    positionRevision: 1,
    playId: 'play-1',
    queue: [track],
    currentIndex: 0,
    positionSeconds: 42,
    isPlaying: true,
    shuffle: false,
    repeat: false,
    ownerSessionId: owner,
    transferTargetSessionId: null,
    devices: owner ? [{ id: owner, name: 'Other browser' }] : [],
  };
}

describe('PlayerService synchronization', () => {
  const other = '00000000-0000-0000-0000-000000000001';
  let current: PlaybackSnapshot;
  let sessionId: string;
  let api: {
    poll: ReturnType<typeof vi.fn>;
    command: ReturnType<typeof vi.fn>;
    leaveOnPageHide: ReturnType<typeof vi.fn>;
  };
  let player: PlayerService;

  beforeEach(() => {
    localStorage.removeItem('afterhours.playback-sync.1');
    FakeAudio.instances = [];
    vi.stubGlobal('Audio', FakeAudio);
    current = snapshot(other);
    sessionId = '';
    api = {
      poll: vi.fn(async (request: { sessionId: string }) => {
        sessionId = request.sessionId;
        return current;
      }),
      command: vi.fn(async (request: PlaybackCommand) => {
        if (request.action === 'toggle')
          current = { ...current, revision: current.revision + 1, isPlaying: !current.isPlaying };
        if (request.action === 'transfer')
          current = {
            ...current,
            revision: current.revision + 1,
            transferTargetSessionId: request.targetSessionId ?? null,
          };
        if (request.action === 'failed')
          current = { ...current, revision: current.revision + 1, isPlaying: false };
        return current;
      }),
      leaveOnPageHide: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthState, useValue: { user: signal({ id: 1 }) } },
        { provide: PlaybackSyncApi, useValue: api },
        {
          provide: MusicLibraryApi,
          useValue: {
            mapTracks: (tracks: MusicTrackResponse[]) =>
              tracks.map((item) => ({
                ...item,
                tileCoverUrl: item.coverUrl,
                albumCoverUrl: item.coverUrl,
              })),
          },
        },
      ],
    });
    player = TestBed.inject(PlayerService);
    TestBed.tick();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    localStorage.removeItem('afterhours.playback-sync.1');
  });

  it('shows and controls another device without playing locally', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    const audio = FakeAudio.instances[0];
    expect(player.isPlaying()).toBe(true);
    expect(audio.play).not.toHaveBeenCalled();

    player.toggle();
    await vi.waitFor(() => expect(api.command).toHaveBeenCalled());
    expect(api.command.mock.calls[0][0].action).toBe('toggle');
    expect(player.isPlaying()).toBe(false);
    expect(audio.play).not.toHaveBeenCalled();
  });

  it('does not start locally until ownership is transferred', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    const audio = FakeAudio.instances[0];
    player.playOnThisDevice();
    await vi.waitFor(() => expect(player.transferPending()).toBe(true));
    expect(audio.play).not.toHaveBeenCalled();

    current = {
      ...current,
      revision: current.revision + 1,
      ownerSessionId: sessionId,
      transferTargetSessionId: null,
    };
    await vi.waitFor(() => expect(audio.play).toHaveBeenCalled(), { timeout: 3500 });
    expect(audio.currentTime).toBe(42);
    expect(player.isThisDevice()).toBe(true);
  });

  it('plays independently after synchronization is disabled', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    const audio = FakeAudio.instances[0];
    await player.setSyncEnabled(false);
    const calls = api.command.mock.calls.length;
    expect(player.syncEnabled()).toBe(false);
    expect(audio.src).toBe('/one');
    expect(player.isPlaying()).toBe(false);
    player.toggle();
    expect(audio.play).toHaveBeenCalled();
    expect(api.command.mock.calls.length).toBe(calls);
  });

  it('resumes independent playback once metadata is loaded', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    await player.setSyncEnabled(false);
    const audio = FakeAudio.instances[0];
    player.play([player.currentTrack()!], 0, 35);
    audio.emit('loadedmetadata');
    expect(audio.currentTime).toBe(35);
    expect(player.currentTime()).toBe(35);
  });

  it('sends the saved start position with a synchronized play command', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    player.play([player.currentTrack()!], 0, 35);
    await vi.waitFor(() => expect(api.command).toHaveBeenCalled());
    expect(api.command.mock.calls[0][0]).toMatchObject({
      action: 'play',
      startPositionSeconds: 35,
    });
  });

  it('pauses at the actual position when a remote device pauses playback', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    current = { ...current, revision: 2, ownerSessionId: sessionId };
    const audio = FakeAudio.instances[0];
    await vi.waitFor(() => expect(audio.play).toHaveBeenCalled(), { timeout: 3500 });
    audio.currentTime = 63;
    current = { ...current, revision: 3, isPlaying: false, positionSeconds: 42 };
    await vi.waitFor(() => expect(player.isPlaying()).toBe(false), { timeout: 3500 });
    expect(audio.currentTime).toBe(63);
    expect(audio.paused).toBe(true);
  });

  it('applies a remote seek to the playing device', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    current = { ...current, revision: 2, ownerSessionId: sessionId };
    const audio = FakeAudio.instances[0];
    await vi.waitFor(() => expect(audio.play).toHaveBeenCalled(), { timeout: 3500 });
    audio.currentTime = 63;
    current = { ...current, revision: 3, positionRevision: 3, positionSeconds: 12 };
    await vi.waitFor(() => expect(audio.currentTime).toBe(12), { timeout: 3500 });
  });

  it('reports a blocked automatic handoff instead of showing it as playing', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    const audio = FakeAudio.instances[0];
    audio.play.mockRejectedValueOnce(new Error('Autoplay blocked'));
    current = { ...current, revision: 2, ownerSessionId: sessionId };
    await vi.waitFor(
      () => expect(api.command.mock.calls.some((call) => call[0].action === 'failed')).toBe(true),
      { timeout: 3500 },
    );
    expect(player.errorMessage()).toContain('Click Play');
    expect(player.isPlaying()).toBe(false);
  });

  it('keeps the existing queue when a position-only update omits it', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    current = { ...current, queue: null, positionSeconds: 53 };
    await vi.waitFor(() => expect(player.currentTime()).toBe(53), { timeout: 3500 });
    expect(player.queue()).toHaveLength(1);
    expect(player.currentTrack()?.id).toBe(1);
  });

  it('reports playback promptly without waiting for the next regular poll', async () => {
    await vi.waitFor(() => expect(player.currentTrack()?.id).toBe(1));
    current = { ...current, revision: 2, ownerSessionId: sessionId };
    const audio = FakeAudio.instances[0];
    await vi.waitFor(() => expect(audio.play).toHaveBeenCalled(), { timeout: 3500 });
    const previousPolls = api.poll.mock.calls.length;
    audio.emit('play');
    await vi.waitFor(() => expect(api.poll.mock.calls.length).toBeGreaterThan(previousPolls), {
      timeout: 1000,
    });
    expect(api.poll.mock.calls.at(-1)?.[0].playId).toBe('play-1');
  });
});
