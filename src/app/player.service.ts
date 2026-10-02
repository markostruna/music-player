import { computed, Injectable, signal } from '@angular/core';
import { MusicTrack } from './music-library-api';

@Injectable({ providedIn: 'root' })
export class PlayerService {
  // The audio element lives in this root service so playback survives route changes.
  private readonly audio = new Audio();

  readonly queue = signal<MusicTrack[]>([]);
  readonly currentIndex = signal(-1);
  readonly currentTrack = computed(() => this.queue()[this.currentIndex()] ?? null);
  readonly hasPrevious = computed(() => this.currentIndex() > 0);
  readonly hasNext = computed(() => this.currentIndex() >= 0 && this.currentIndex() < this.queue().length - 1);
  readonly isPlaying = signal(false);
  readonly currentTime = signal(0);
  readonly duration = signal(0);
  readonly errorMessage = signal('');

  constructor() {
    this.audio.preload = 'metadata';
    this.audio.volume = 0.72;
    this.audio.addEventListener('play', () => this.isPlaying.set(true));
    this.audio.addEventListener('pause', () => this.isPlaying.set(false));
    this.audio.addEventListener('timeupdate', () => this.syncTime());
    this.audio.addEventListener('loadedmetadata', () => this.syncTime());
    this.audio.addEventListener('ended', () => {
      if (this.hasNext()) this.next();
      else this.isPlaying.set(false);
    });
  }

  play(queue: MusicTrack[], index: number): void {
    if (index < 0 || index >= queue.length) return;
    this.queue.set([...queue]);
    this.startIndex(index);
  }

  playFromQueue(index: number): void {
    if (index >= 0 && index < this.queue().length) this.startIndex(index);
  }

  next(): void {
    if (this.hasNext()) this.startIndex(this.currentIndex() + 1);
  }

  previous(): void {
    if (this.audio.currentTime > 3 || !this.hasPrevious()) {
      this.audio.currentTime = 0;
      this.currentTime.set(0);
    } else {
      this.startIndex(this.currentIndex() - 1);
    }
  }

  toggle(): void {
    if (!this.currentTrack()) return;
    if (this.audio.paused) {
      void this.audio.play().catch(() => this.errorMessage.set('Playback could not start.'));
    } else {
      this.audio.pause();
    }
  }

  seek(seconds: number): void {
    this.audio.currentTime = seconds;
    this.currentTime.set(seconds);
  }

  setVolume(percent: number): void {
    this.audio.volume = Math.min(1, Math.max(0, percent / 100));
  }

  stop(): void {
    this.audio.pause();
    this.audio.removeAttribute('src');
    this.audio.load();
    this.queue.set([]);
    this.currentIndex.set(-1);
    this.currentTime.set(0);
    this.duration.set(0);
    this.errorMessage.set('');
  }

  private startIndex(index: number): void {
    const track = this.queue()[index];
    this.currentIndex.set(index);
    this.currentTime.set(0);
    this.duration.set(track.durationSeconds);
    this.errorMessage.set('');
    this.audio.pause();
    this.audio.src = track.streamUrl;
    this.audio.load();
    void this.audio.play().catch(() => {
      this.isPlaying.set(false);
      this.errorMessage.set('This track could not be played in this browser.');
    });
  }

  private syncTime(): void {
    this.currentTime.set(this.audio.currentTime);
    this.duration.set(Number.isFinite(this.audio.duration) ? this.audio.duration : this.currentTrack()?.durationSeconds ?? 0);
  }
}
