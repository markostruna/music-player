import { computed, DestroyRef, effect, inject, Injectable, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { DEMO_MODE } from '../utils/api-path';
import { AuthState } from './auth-state';
import { MusicLibraryApi, MusicTrack } from './music-library-api';
import {
  PlaybackCommand,
  PlaybackDevice,
  PlaybackSnapshot,
  PlaybackSyncApi,
} from './playback-sync-api';

@Injectable({ providedIn: 'root' })
export class PlayerService {
  private readonly audio = new Audio();
  private readonly auth = inject(AuthState);
  private readonly library = inject(MusicLibraryApi);
  private readonly syncApi = inject(PlaybackSyncApi);
  private readonly sessionId = crypto.randomUUID();
  private readonly deviceName = `${this.browserName()} on ${navigator.platform || 'device'} (${this.sessionId.slice(0, 4)})`;
  private userId: number | null = null;
  private epoch = 0;
  private revision = -1;
  private positionRevision = -1;
  private loadedPlayId = '';
  private owner: string | null = null;
  private transferTarget: string | null = null;
  private loadedTrackId: number | null = null;
  private pendingPosition: number | null = null;
  private lastSuccessfulPoll = 0;
  private timer: ReturnType<typeof setInterval> | null = null;
  private seekTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingPoll = false;
  private pollAgain = false;
  private operations: Promise<void> = Promise.resolve();
  private audioGeneration = 0;

  readonly queue = signal<MusicTrack[]>([]);
  readonly currentIndex = signal(-1);
  readonly shuffle = signal(false);
  readonly repeat = signal(false);
  readonly currentTrack = computed(
    (): MusicTrack | null => this.queue()[this.currentIndex()] ?? null,
  );
  readonly hasPrevious = computed(() => this.currentIndex() > 0);
  readonly hasNext = computed(
    () =>
      this.currentIndex() >= 0 && (this.shuffle() || this.currentIndex() < this.queue().length - 1),
  );
  readonly isPlaying = signal(false);
  readonly currentTime = signal(0);
  readonly duration = signal(0);
  readonly volumePercent = signal(72);
  readonly errorMessage = signal('');
  readonly syncEnabled = signal(!DEMO_MODE);
  readonly devices = signal<PlaybackDevice[]>([]);
  readonly playingDevice = computed(
    () => this.devices().find((device) => device.id === this.ownerId())?.name ?? 'No device',
  );
  readonly ownerId = signal<string | null>(null);
  readonly isThisDevice = computed(() => this.ownerId() === this.sessionId);
  readonly transferPending = signal(false);

  constructor() {
    this.audio.preload = 'metadata';
    this.audio.volume = 0.72;
    this.audio.addEventListener('play', () => {
      if (!this.syncEnabled() || DEMO_MODE) this.isPlaying.set(true);
      if (this.isSynchronized() && this.isThisDevice()) {
        queueMicrotask(() => {
          if (this.pendingPoll) this.pollAgain = true;
          else this.poll();
        });
      }
      if (
        this.errorMessage().startsWith('Playback was blocked.') ||
        this.errorMessage().startsWith('Click Play to allow playback') ||
        this.errorMessage().startsWith('Playback paused because synchronization')
      )
        this.errorMessage.set('');
    });
    this.audio.addEventListener('pause', () => {
      if (!this.syncEnabled() || DEMO_MODE) this.isPlaying.set(false);
    });
    this.audio.addEventListener('timeupdate', () => {
      if (!this.syncEnabled() || DEMO_MODE || this.isThisDevice()) this.syncTime();
    });
    this.audio.addEventListener('loadedmetadata', () => {
      if (this.pendingPosition !== null) {
        this.audio.currentTime = Math.min(
          this.pendingPosition,
          this.audio.duration || this.pendingPosition,
        );
        this.pendingPosition = null;
      }
      if (!this.isSynchronized() || this.isThisDevice()) this.syncTime();
    });
    this.audio.addEventListener('ended', () => {
      if (this.isSynchronized()) this.sendCommand('ended', { expectedPlayId: this.loadedPlayId });
      else if (this.repeat()) this.startIndex(this.currentIndex());
      else if (this.shuffle()) this.startRandomTrack();
      else if (this.hasNext()) this.next();
      else this.isPlaying.set(false);
    });
    const pageHide = (event: PageTransitionEvent) => {
      if (!this.isSynchronized() || this.revision < 0) return;
      if (!event.persisted) {
        this.syncApi.leaveOnPageHide({
          sessionId: this.sessionId,
          revision: this.revision,
          action: 'leave',
          ...(this.isThisDevice()
            ? { positionSeconds: this.safeAudioTime(), wasPlaying: !this.audio.paused }
            : {}),
        });
      }
      this.audioGeneration++;
      this.audio.pause();
    };
    const pageShow = () => {
      if (this.isSynchronized()) this.poll();
    };
    const storage = (event: StorageEvent) => {
      if (this.userId === null || event.key !== this.preferenceKey(this.userId)) return;
      void this.setSyncEnabled(event.newValue !== 'false').catch(() => {
        this.errorMessage.set('The playback synchronization setting could not be applied.');
      });
    };
    window.addEventListener('pagehide', pageHide);
    window.addEventListener('pageshow', pageShow);
    window.addEventListener('storage', storage);
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('pagehide', pageHide);
      window.removeEventListener('pageshow', pageShow);
      window.removeEventListener('storage', storage);
      this.endSynchronization();
      this.audio.pause();
    });

    effect(() => {
      const userId = this.auth.user()?.id ?? null;
      if (userId === this.userId) return;
      this.endSynchronization();
      if (this.userId !== null) this.stop();
      this.userId = userId;
      if (userId === null || DEMO_MODE) return;
      this.syncEnabled.set(localStorage.getItem(this.preferenceKey(userId)) !== 'false');
      if (this.syncEnabled()) this.beginSynchronization();
    });
  }

  async setSyncEnabled(enabled: boolean): Promise<void> {
    if (DEMO_MODE || this.userId === null || enabled === this.syncEnabled()) return;
    localStorage.setItem(this.preferenceKey(this.userId), String(enabled));
    if (!enabled) {
      const wasOwner = this.isThisDevice();
      try {
        await this.leave();
      } finally {
        this.endSynchronization();
        this.syncEnabled.set(false);
        const track = this.currentTrack();
        if (!wasOwner && track) {
          this.audio.pause();
          this.loadedTrackId = track.id;
          this.pendingPosition = this.currentTime();
          this.audio.src = track.streamUrl;
          this.audio.load();
        }
        this.isPlaying.set(!this.audio.paused);
      }
    } else {
      this.audio.pause();
      this.syncEnabled.set(true);
      this.beginSynchronization();
    }
  }

  play(queue: MusicTrack[], index: number, startPositionSeconds = 0): void {
    if (index < 0 || index >= queue.length) return;
    if (this.isSynchronized()) {
      const positionSeconds = this.safeAudioTime();
      const wasPlaying = !this.audio.paused;
      if (this.isThisDevice() && !this.transferTarget) this.preparePlayback(queue[index], startPositionSeconds);
      this.sendCommand('play', {
        trackIds: queue.map((track) => track.id),
        index,
        positionSeconds,
        wasPlaying,
        ...(startPositionSeconds > 0 ? { startPositionSeconds } : {}),
      });
      return;
    }
    this.queue.set([...queue]);
    this.startIndex(index, startPositionSeconds);
  }

  enqueue(tracks: MusicTrack[]): void {
    if (!tracks.length) return;
    if (this.isSynchronized()) {
      const positionSeconds = this.safeAudioTime();
      const wasPlaying = !this.audio.paused;
      if (!this.queue().length && this.isThisDevice()) this.preparePlayback(tracks[0], 0);
      this.sendCommand('enqueue', {
        trackIds: tracks.map((track) => track.id),
        index: 0,
        positionSeconds,
        wasPlaying,
      });
      return;
    }
    if (!this.queue().length) {
      this.play(tracks, 0);
      return;
    }
    this.queue.update((queue) => [...queue, ...tracks]);
  }

  playFromQueue(index: number): void {
    if (index < 0 || index >= this.queue().length) return;
    if (this.isSynchronized()) {
      const positionSeconds = this.safeAudioTime();
      const wasPlaying = !this.audio.paused;
      if (this.isThisDevice() && !this.transferTarget) this.preparePlayback(this.queue()[index], 0);
      this.sendCommand('index', { index, positionSeconds, wasPlaying });
    } else this.startIndex(index);
  }

  next(): void {
    if (this.isSynchronized()) this.sendCommand('next');
    else if (this.shuffle()) this.startRandomTrack();
    else if (this.hasNext()) this.startIndex(this.currentIndex() + 1);
  }

  previous(): void {
    if (this.isSynchronized()) {
      this.sendCommand('previous');
    } else if (this.audio.currentTime > 3 || !this.hasPrevious()) {
      this.audio.currentTime = 0;
      this.currentTime.set(0);
    } else this.startIndex(this.currentIndex() - 1);
  }

  toggle(): void {
    if (!this.currentTrack()) return;
    if (this.isSynchronized()) {
      if (!this.isPlaying() && this.isThisDevice() && !this.transferTarget) {
        void this.audio
          .play()
          .catch(() => this.errorMessage.set('Click Play to allow playback on this device.'));
      }
      this.sendCommand('toggle');
    } else if (this.audio.paused) {
      void this.audio.play().catch(() => this.errorMessage.set('Playback could not start.'));
    } else this.audio.pause();
  }

  seek(seconds: number): void {
    if (this.isSynchronized()) {
      this.currentTime.set(seconds);
      if (this.seekTimer !== null) clearTimeout(this.seekTimer);
      this.seekTimer = setTimeout(() => {
        this.seekTimer = null;
        this.sendCommand('seek', { positionSeconds: seconds });
      }, 150);
    } else {
      this.audio.currentTime = seconds;
      this.currentTime.set(seconds);
    }
  }

  toggleShuffle(): void {
    if (this.isSynchronized()) this.sendCommand('shuffle');
    else this.shuffle.update((enabled) => !enabled);
  }

  toggleRepeat(): void {
    if (this.isSynchronized()) this.sendCommand('repeat');
    else this.repeat.update((enabled) => !enabled);
  }

  playOnThisDevice(): void {
    if (this.isSynchronized()) {
      this.sendCommand('transfer', { targetSessionId: this.sessionId, startPlaying: true });
    }
  }

  transferTo(sessionId: string): void {
    if (this.isSynchronized()) this.sendCommand('transfer', { targetSessionId: sessionId });
  }

  setVolume(percent: number): void {
    const volume = Math.min(1, Math.max(0, percent / 100));
    this.audio.volume = volume;
    this.volumePercent.set(volume * 100);
  }

  async disconnect(): Promise<void> {
    try {
      if (this.isSynchronized()) await this.leave();
    } finally {
      this.endSynchronization();
      this.stop();
    }
  }

  stop(): void {
    this.audioGeneration++;
    this.audio.pause();
    this.audio.removeAttribute('src');
    this.audio.load();
    this.loadedTrackId = null;
    this.queue.set([]);
    this.currentIndex.set(-1);
    this.currentTime.set(0);
    this.duration.set(0);
    this.isPlaying.set(false);
    this.errorMessage.set('');
  }

  private isSynchronized(): boolean {
    return !DEMO_MODE && this.syncEnabled() && this.userId !== null;
  }

  private preferenceKey(userId: number): string {
    return `afterhours.playback-sync.${userId}`;
  }

  private browserName(): string {
    const agent = navigator.userAgent;
    if (agent.includes('Edg/')) return 'Edge';
    if (agent.includes('Firefox/')) return 'Firefox';
    if (agent.includes('Chrome/')) return 'Chrome';
    if (agent.includes('Safari/')) return 'Safari';
    return 'Browser';
  }

  private beginSynchronization(): void {
    this.revision = -1;
    this.positionRevision = -1;
    this.lastSuccessfulPoll = Date.now();
    this.poll();
    this.timer = setInterval(() => {
      if (this.isThisDevice() && Date.now() - this.lastSuccessfulPoll > 12000) {
        this.audioGeneration++;
        this.audio.pause();
        this.errorMessage.set('Playback paused because synchronization was interrupted.');
      }
      this.poll();
    }, 2000);
  }

  private endSynchronization(): void {
    this.epoch++;
    if (this.timer !== null) clearInterval(this.timer);
    if (this.seekTimer !== null) clearTimeout(this.seekTimer);
    this.timer = null;
    this.seekTimer = null;
    this.pendingPoll = false;
    this.pollAgain = false;
    this.owner = null;
    this.transferTarget = null;
    this.ownerId.set(null);
    this.transferPending.set(false);
    this.devices.set([]);
  }

  private poll(): void {
    if (!this.isSynchronized() || this.pendingPoll) return;
    this.pendingPoll = true;
    const epoch = this.epoch;
    this.schedule(async () => {
      if (epoch !== this.epoch || !this.isSynchronized()) return;
      try {
        const snapshot = await this.syncApi.poll({
          sessionId: this.sessionId,
          deviceName: this.deviceName,
          revision: this.revision,
          positionSeconds: this.isThisDevice() ? this.safeAudioTime() : 0,
          isPlaying: this.isThisDevice() && !this.audio.paused,
          transferStopped: this.isThisDevice() && this.transferTarget !== null && this.audio.paused,
          playId: this.loadedPlayId,
        });
        if (epoch !== this.epoch) return;
        this.lastSuccessfulPoll = Date.now();
        if (this.errorMessage() === 'Playback synchronization failed. Reconnecting…')
          this.errorMessage.set('');
        this.applySnapshot(snapshot);
      } finally {
        if (epoch === this.epoch) {
          this.pendingPoll = false;
          if (this.pollAgain) {
            this.pollAgain = false;
            this.poll();
          }
        }
      }
    });
  }

  private sendCommand(action: string, values: Partial<PlaybackCommand> = {}): void {
    if (
      ['play', 'index', 'next', 'previous', 'ended'].includes(action) &&
      this.seekTimer !== null
    ) {
      clearTimeout(this.seekTimer);
      this.seekTimer = null;
    }
    const epoch = this.epoch;
    this.schedule(async () => {
      if (epoch !== this.epoch || !this.isSynchronized()) return;
      if (
        (action === 'ended' || action === 'failed') &&
        values.expectedPlayId !== this.loadedPlayId
      )
        return;
      let snapshot: PlaybackSnapshot;
      try {
        snapshot = await this.syncApi.command({
          sessionId: this.sessionId,
          revision: this.revision,
          action,
          ...(this.isThisDevice() && action !== 'seek'
            ? { positionSeconds: this.safeAudioTime(), wasPlaying: !this.audio.paused }
            : {}),
          ...values,
        });
      } catch (error) {
        if (
          (action === 'ended' || action === 'failed') &&
          error instanceof HttpErrorResponse &&
          error.status === 409
        ) {
          this.poll();
          return;
        }
        if (this.isThisDevice()) this.audio.pause();
        throw error;
      }
      if (epoch === this.epoch) {
        if (action !== 'failed') this.errorMessage.set('');
        this.applySnapshot(snapshot);
      }
    });
  }

  private async leave(): Promise<void> {
    const epoch = this.epoch;
    const operation = this.operations.then(async () => {
      if (epoch !== this.epoch || this.revision < 0) return;
      await this.syncApi.command({
        sessionId: this.sessionId,
        revision: this.revision,
        action: 'leave',
        ...(this.isThisDevice()
          ? { positionSeconds: this.safeAudioTime(), wasPlaying: !this.audio.paused }
          : {}),
      });
    });
    this.operations = operation.catch(() => undefined);
    await operation;
  }

  private schedule(operation: () => Promise<void>): void {
    this.operations = this.operations.then(operation).catch((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        (error.status === 400 || error.status === 409) &&
        typeof error.error === 'string'
      ) {
        this.errorMessage.set(error.error);
      } else {
        this.errorMessage.set('Playback synchronization failed. Reconnecting…');
      }
    });
  }

  private applySnapshot(snapshot: PlaybackSnapshot): void {
    const previousRevision = this.revision;
    if (snapshot.revision !== previousRevision && snapshot.queue === null)
      throw new Error('Playback response is missing its updated queue.');
    const positionChanged = snapshot.positionRevision !== this.positionRevision;
    const playChanged = snapshot.playId !== this.loadedPlayId;
    const wasOwner = this.owner === this.sessionId;
    this.revision = snapshot.revision;
    this.positionRevision = snapshot.positionRevision;
    this.loadedPlayId = snapshot.playId;
    this.owner = snapshot.ownerSessionId;
    this.transferTarget = snapshot.transferTargetSessionId;
    this.ownerId.set(this.owner);
    this.transferPending.set(this.transferTarget !== null);
    this.devices.set(snapshot.devices);
    if (snapshot.revision !== previousRevision) {
      if (snapshot.queue !== null) this.queue.set(this.library.mapTracks(snapshot.queue));
      this.currentIndex.set(snapshot.currentIndex);
      this.shuffle.set(snapshot.shuffle);
      this.repeat.set(snapshot.repeat);
    }
    this.isPlaying.set(snapshot.isPlaying);
    this.currentTime.set(snapshot.positionSeconds);
    this.duration.set(this.currentTrack()?.durationSeconds ?? 0);

    if (this.owner !== this.sessionId || this.transferTarget !== null) {
      if (wasOwner || !this.audio.paused) {
        this.audioGeneration++;
        this.audio.pause();
      }
      return;
    }
    const track = this.currentTrack();
    if (!track) {
      this.audio.pause();
      return;
    }
    const changed = this.loadedTrackId !== track.id;
    if (changed) {
      this.audioGeneration++;
      this.audio.pause();
      this.loadedTrackId = track.id;
      this.audio.src = track.streamUrl;
      this.audio.load();
    }
    if (changed || !wasOwner || positionChanged || playChanged) {
      this.pendingPosition = snapshot.positionSeconds;
      if (this.audio.readyState > 0) {
        this.audio.currentTime = snapshot.positionSeconds;
        this.pendingPosition = null;
      }
    }
    if (!snapshot.isPlaying) {
      this.audioGeneration++;
      this.audio.pause();
    } else if (this.audio.ended && !changed && !playChanged) {
      this.sendCommand('ended', { expectedPlayId: snapshot.playId });
    } else if (this.audio.paused) {
      const generation = ++this.audioGeneration;
      void this.audio
        .play()
        .then(() => {
          if (
            generation === this.audioGeneration &&
            this.errorMessage().startsWith('Playback was blocked.')
          )
            this.errorMessage.set('');
        })
        .catch(() => {
          if (generation !== this.audioGeneration || !this.isThisDevice()) return;
          this.errorMessage.set('Playback was blocked. Click Play to start on this device.');
          this.sendCommand('failed', { expectedPlayId: snapshot.playId });
        });
    }
  }

  private preparePlayback(track: MusicTrack, seconds: number): void {
    this.audioGeneration++;
    this.audio.pause();
    this.loadedTrackId = track.id;
    this.pendingPosition = seconds;
    this.audio.src = track.streamUrl;
    this.audio.load();
    void this.audio.play().catch(() => {
      this.errorMessage.set('Playback was blocked. Click Play to start on this device.');
    });
  }

  private safeAudioTime(): number {
    return Number.isFinite(this.audio.currentTime) ? Math.max(0, this.audio.currentTime) : 0;
  }

  private startIndex(index: number, startPositionSeconds = 0): void {
    const track = this.queue()[index];
    this.currentIndex.set(index);
    this.currentTime.set(startPositionSeconds);
    this.duration.set(track.durationSeconds);
    this.errorMessage.set('');
    this.audio.pause();
    this.loadedTrackId = track.id;
    this.pendingPosition = startPositionSeconds;
    this.audio.src = track.streamUrl;
    this.audio.load();
    void this.audio.play().catch(() => {
      this.isPlaying.set(false);
      this.errorMessage.set('This track could not be played in this browser.');
    });
  }

  private startRandomTrack(): void {
    const queue = this.queue();
    if (!queue.length) return;
    const candidates = queue
      .map((_, index) => index)
      .filter((index) => index !== this.currentIndex());
    this.startIndex(
      candidates[Math.floor(Math.random() * candidates.length)] ?? this.currentIndex(),
    );
  }

  private syncTime(): void {
    this.currentTime.set(this.safeAudioTime());
    this.duration.set(
      Number.isFinite(this.audio.duration)
        ? this.audio.duration
        : (this.currentTrack()?.durationSeconds ?? 0),
    );
  }
}
