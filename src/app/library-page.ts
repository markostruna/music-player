import { Component, computed, ElementRef, inject, OnInit, signal, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthState, ThemeName } from './auth-state';
import { MetadataPatch, MusicLibraryApi, MusicTrack } from './music-library-api';

type LibraryView = 'tracks' | 'albums' | 'artists';

interface AlbumTile {
  key: string;
  title: string;
  artist: string;
  tracks: MusicTrack[];
  coverUrl: string;
}

interface ArtistTile {
  key: string;
  name: string;
  tracks: MusicTrack[];
  albumCount: number;
  coverUrl: string;
}

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
  readonly collectionView = signal<LibraryView>('tracks');
  readonly currentPage = signal(1);
  readonly pageSize = 100;
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
  readonly albums = computed(() => {
    const groups = new Map<string, AlbumTile>();
    for (const track of this.tracks()) {
      const artist = track.albumArtist.trim() || track.artist;
      const key = `${artist.toLowerCase()}\u0000${track.album.toLowerCase()}`;
      const group = groups.get(key);
      if (group) {
        group.tracks.push(track);
      } else {
        groups.set(key, { key, title: track.album, artist, tracks: [track], coverUrl: track.coverUrl });
      }
    }
    return [...groups.values()].sort((first, second) =>
      first.artist.localeCompare(second.artist) || first.title.localeCompare(second.title));
  });
  readonly filteredAlbums = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    return this.albums().filter((album) => `${album.title} ${album.artist}`.toLowerCase().includes(query));
  });
  readonly artists = computed(() => {
    const groups = new Map<string, ArtistTile & { albumKeys: Set<string> }>();
    for (const track of this.tracks()) {
      const name = track.artist.trim() || 'Unknown artist';
      const key = name.toLowerCase();
      let group = groups.get(key);
      if (!group) {
        group = { key, name, tracks: [], albumCount: 0, coverUrl: track.coverUrl, albumKeys: new Set<string>() };
        groups.set(key, group);
      }
      group.tracks.push(track);
      group.albumKeys.add(`${track.albumArtist.trim() || track.artist}\u0000${track.album}`.toLowerCase());
    }
    return [...groups.values()]
      .map(({ albumKeys, ...artist }) => ({ ...artist, albumCount: albumKeys.size }))
      .sort((first, second) => first.name.localeCompare(second.name));
  });
  readonly filteredArtists = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    return this.artists().filter((artist) => artist.name.toLowerCase().includes(query));
  });
  readonly totalItems = computed(() => {
    switch (this.collectionView()) {
      case 'albums': return this.filteredAlbums().length;
      case 'artists': return this.filteredArtists().length;
      default: return this.filteredTracks().length;
    }
  });
  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalItems() / this.pageSize)));
  readonly pageNumber = computed(() => Math.min(this.currentPage(), this.totalPages()));
  readonly pageStart = computed(() => this.totalItems() === 0 ? 0 : (this.pageNumber() - 1) * this.pageSize + 1);
  readonly pageEnd = computed(() => Math.min(this.pageNumber() * this.pageSize, this.totalItems()));
  readonly visibleTracks = computed(() => this.pageSlice(this.filteredTracks()));
  readonly visibleAlbums = computed(() => this.pageSlice(this.filteredAlbums()));
  readonly visibleArtists = computed(() => this.pageSlice(this.filteredArtists()));
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

  setCollectionView(view: LibraryView): void {
    this.collectionView.set(view);
    this.currentPage.set(1);
    this.selectedTrackIds.set(new Set<number>());
  }

  setSearchQuery(query: string): void {
    this.searchQuery.set(query);
    this.currentPage.set(1);
  }

  previousPage(): void {
    this.currentPage.update((page) => Math.max(1, page - 1));
  }

  nextPage(): void {
    this.currentPage.update((page) => Math.min(this.totalPages(), page + 1));
  }

  playGroup(tracks: MusicTrack[]): void {
    const firstTrack = tracks[0];
    if (firstTrack) this.chooseTrack(firstTrack);
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

  private pageSlice<T>(items: T[]): T[] {
    const start = (this.pageNumber() - 1) * this.pageSize;
    return items.slice(start, start + this.pageSize);
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
