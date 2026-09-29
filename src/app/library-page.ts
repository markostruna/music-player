import { Component, computed, ElementRef, inject, OnInit, signal, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthState, ThemeName } from './auth-state';
import { MetadataPatch, MusicLibraryApi, MusicTrack } from './music-library-api';

@Component({
  selector: 'app-library-page',
  imports: [FormsModule, RouterLink],
  templateUrl: './library-page.html',
})
export class LibraryPage implements OnInit {
  private readonly auth = inject(AuthState);
  private readonly router = inject(Router);
  private readonly library = inject(MusicLibraryApi);

  @ViewChild('audioPlayer') private audioPlayer?: ElementRef<HTMLAudioElement>;

  readonly activeSection = signal('Listen now');
  readonly searchQuery = signal('');
  readonly isPlaying = signal(false);
  readonly currentTime = signal(0);
  readonly duration = signal(0);
  readonly isLoading = signal(true);
  readonly errorMessage = signal('');
  readonly tracks = signal<MusicTrack[]>([]);
  readonly currentTrack = signal<MusicTrack | null>(null);
  readonly isAdmin = this.auth.isAdmin;
  readonly user = this.auth.user;
  readonly selectedTrackIds = signal(new Set<number>());
  readonly hasSelection = computed(() => this.selectedTrackIds().size > 0);
  readonly filteredTracks = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    return this.tracks().filter((track) => `${track.title} ${track.artist} ${track.album}`.toLowerCase().includes(query));
  });
  readonly themeOptions: ThemeName[] = ['Light', 'Dark', 'Blue'];

  metadata: MetadataPatch = {};
  destinationFolderId: number | null = null;
  folders: Awaited<ReturnType<MusicLibraryApi['listFolders']>> = [];
  operationMessage = '';

  ngOnInit(): void {
    void this.loadLibrary();
  }

  async loadLibrary(): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      this.tracks.set(await this.library.listTracks());
      if (this.isAdmin()) this.folders = await this.library.listFolders();
    } catch {
      this.errorMessage.set('The library could not be loaded. Check the music server connection.');
    } finally {
      this.isLoading.set(false);
    }
  }

  chooseTrack(track: MusicTrack): void {
    this.currentTrack.set(track);
    this.currentTime.set(0);
    const audio = this.audioPlayer?.nativeElement;
    if (!audio) return;
    audio.pause();
    audio.src = track.streamUrl;
    audio.load();
    void audio.play().then(() => this.isPlaying.set(true)).catch(() => {
      this.isPlaying.set(false);
      this.errorMessage.set('This track could not be played in this browser.');
    });
  }

  playFirstTrack(): void {
    const firstTrack = this.tracks()[0];
    if (firstTrack) this.chooseTrack(firstTrack);
  }

  togglePlayback(): void {
    const audio = this.audioPlayer?.nativeElement;
    if (!audio) {
      this.playFirstTrack();
      return;
    }
    if (audio.paused) {
      void audio.play().then(() => this.isPlaying.set(true)).catch(() => this.errorMessage.set('Playback could not start.'));
    } else {
      audio.pause();
      this.isPlaying.set(false);
    }
  }

  playAdjacentTrack(direction: -1 | 1): void {
    const tracks = this.tracks();
    const index = tracks.findIndex((track) => track.id === this.currentTrack()?.id);
    const nextIndex = index + direction;
    if (nextIndex >= 0 && nextIndex < tracks.length) this.chooseTrack(tracks[nextIndex]);
  }

  updatePlaybackTime(event: Event): void {
    const audio = event.target as HTMLAudioElement;
    this.currentTime.set(audio.currentTime);
    this.duration.set(Number.isFinite(audio.duration) ? audio.duration : this.currentTrack()?.durationSeconds ?? 0);
  }

  seek(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    if (this.audioPlayer) this.audioPlayer.nativeElement.currentTime = value;
    this.currentTime.set(value);
  }

  setVolume(event: Event): void {
    if (this.audioPlayer) this.audioPlayer.nativeElement.volume = Number((event.target as HTMLInputElement).value) / 100;
  }

  formatTime(seconds: number): string {
    if (!Number.isFinite(seconds)) return '0:00';
    return `${Math.floor(seconds / 60)}:${Math.floor(seconds % 60).toString().padStart(2, '0')}`;
  }

  toggleSelection(trackId: number, event: Event): void {
    const selected = new Set(this.selectedTrackIds());
    if ((event.target as HTMLInputElement).checked) selected.add(trackId);
    else selected.delete(trackId);
    this.selectedTrackIds.set(selected);
    this.operationMessage = '';
  }

  async saveMetadata(): Promise<void> {
    const ids = [...this.selectedTrackIds()];
    const changes = Object.fromEntries(Object.entries(this.metadata).filter(([, value]) => value !== '' && value !== undefined));
    if (ids.length === 0 || Object.keys(changes).length === 0) return;
    try {
      const results = await this.library.updateMetadata(ids, changes);
      const succeeded = results.filter((result) => result.succeeded).length;
      this.operationMessage = `Updated tags for ${succeeded} of ${results.length} selected tracks.`;
      this.metadata = {};
      await this.loadLibrary();
    } catch {
      this.operationMessage = 'The tag update could not be completed.';
    }
  }

  async updateCover(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    const ids = [...this.selectedTrackIds()];
    if (!file || ids.length === 0) return;
    try {
      const results = await this.library.updateCover(ids, file);
      const succeeded = results.filter((result) => result.succeeded).length;
      this.operationMessage = `Updated cover art for ${succeeded} of ${results.length} selected tracks.`;
      await this.loadLibrary();
    } catch {
      this.operationMessage = 'The image could not be embedded. Use a PNG, JPEG, GIF, or WebP image under 10 MB.';
    } finally {
      input.value = '';
    }
  }

  setDestinationFolder(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.destinationFolderId = value ? Number(value) : null;
  }

  async moveSelected(): Promise<void> {
    if (!this.destinationFolderId) return;
    try {
      const results = await this.library.moveTracks([...this.selectedTrackIds()], this.destinationFolderId);
      const succeeded = results.filter((result) => result.succeeded).length;
      this.operationMessage = `Moved ${succeeded} of ${results.length} selected tracks.`;
      await this.loadLibrary();
    } catch {
      this.operationMessage = 'The selected tracks could not be moved.';
    }
  }

  async changeTheme(event: Event): Promise<void> {
    try {
      await this.auth.setTheme((event.target as HTMLSelectElement).value as ThemeName);
    } catch {
      this.errorMessage.set('The theme preference could not be saved.');
    }
  }

  async signOut(): Promise<void> {
    await this.auth.signOut();
    void this.router.navigateByUrl('/login');
  }
}
