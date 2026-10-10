import {
  AfterViewInit,
  Component,
  computed,
  inject,
  OnDestroy,
  OnInit,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthState } from '@shared/services/auth-state';
import { formatTime } from '@shared/utils/format-time';
import { LibraryStore, LibraryView } from '@shared/services/library-store';
import { MetadataPatch, MusicLibraryApi, MusicTrack } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { MusicShellState } from '../../shell/music-shell/music-shell-state';

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
  artistImageUrl: string;
  artistImageMissing: boolean;
}

@Component({
  selector: 'app-library-page',
  imports: [FormsModule, RouterLink],
  templateUrl: './library-page.html',
})
export class LibraryPage implements OnInit, AfterViewInit, OnDestroy {
  private readonly auth = inject(AuthState);
  private readonly library = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  private readonly shellState = inject(MusicShellState);

  readonly activeSection = this.shellState.activeSection;
  readonly searchQuery = this.store.searchQuery;
  readonly collectionView = this.store.collectionView;
  readonly currentPage = this.store.currentPage;
  readonly pageSize = 100;
  readonly isPlaying = this.player.isPlaying;
  readonly isLoading = this.store.isLoading;
  private readonly pageError = signal('');
  readonly errorMessage = computed(() => this.store.errorMessage() || this.pageError());
  readonly tracks = this.store.tracks;
  readonly currentTrack = this.player.currentTrack;
  readonly isAdmin = this.auth.isAdmin;
  readonly selectedTrackIds = signal(new Set<number>());
  readonly hasSelection = computed(() => this.selectedTrackIds().size > 0);
  readonly filteredTracks = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    return this.tracks().filter((track) =>
      `${track.title} ${track.artist} ${track.album}`.toLowerCase().includes(query),
    );
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
        groups.set(key, {
          key,
          title: track.album,
          artist,
          tracks: [track],
          coverUrl: track.coverUrl,
        });
      }
    }
    return [...groups.values()].sort(
      (first, second) =>
        first.artist.localeCompare(second.artist) || first.title.localeCompare(second.title),
    );
  });
  readonly filteredAlbums = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    return this.albums().filter((album) =>
      `${album.title} ${album.artist}`.toLowerCase().includes(query),
    );
  });
  readonly artists = computed(() => {
    const groups = new Map<string, ArtistTile & { albumKeys: Set<string> }>();
    for (const track of this.tracks()) {
      const name = track.artist.trim() || 'Unknown artist';
      const key = name.toLowerCase();
      let group = groups.get(key);
      if (!group) {
        group = {
          key,
          name,
          tracks: [],
          albumCount: 0,
          coverUrl: track.coverUrl,
          artistImageUrl: track.artistImageUrl,
          artistImageMissing: track.artistImageMissing,
          albumKeys: new Set<string>(),
        };
        groups.set(key, group);
      }
      group.tracks.push(track);
      group.albumKeys.add(
        `${track.albumArtist.trim() || track.artist}\u0000${track.album}`.toLowerCase(),
      );
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
      case 'albums':
        return this.filteredAlbums().length;
      case 'artists':
        return this.filteredArtists().length;
      default:
        return this.filteredTracks().length;
    }
  });
  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalItems() / this.pageSize)));
  readonly pageNumber = computed(() => Math.min(this.currentPage(), this.totalPages()));
  readonly pageStart = computed(() =>
    this.totalItems() === 0 ? 0 : (this.pageNumber() - 1) * this.pageSize + 1,
  );
  readonly pageEnd = computed(() => Math.min(this.pageNumber() * this.pageSize, this.totalItems()));
  readonly visibleTracks = computed(() => this.pageSlice(this.filteredTracks()));
  readonly visibleAlbums = computed(() => this.pageSlice(this.filteredAlbums()));
  readonly visibleArtists = computed(() => this.pageSlice(this.filteredArtists()));
  metadata: MetadataPatch = {};
  destinationFolderId: number | null = null;
  folders: Awaited<ReturnType<MusicLibraryApi['listFolders']>> = [];
  operationMessage = '';

  readonly formatTime = formatTime;

  ngOnInit(): void {
    void this.loadLibrary(false);
  }

  ngAfterViewInit(): void {
    window.scrollTo(0, this.store.scrollTop);
  }

  ngOnDestroy(): void {
    this.store.scrollTop = window.scrollY;
  }

  async loadLibrary(forceRefresh = true): Promise<void> {
    this.pageError.set('');
    if (forceRefresh) await this.store.refresh();
    else await this.store.ensureLoaded();
    if (this.isAdmin() && !this.store.errorMessage()) {
      try {
        this.folders = await this.library.listFolders();
      } catch {
        this.pageError.set('The music folders could not be loaded.');
      }
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
    this.player.play(tracks, 0);
  }

  chooseTrack(track: MusicTrack): void {
    const queue = this.filteredTracks();
    this.player.play(
      queue,
      queue.findIndex((candidate) => candidate.id === track.id),
    );
  }

  playFirstTrack(): void {
    this.player.play(this.tracks(), 0);
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
    const changes = Object.fromEntries(
      Object.entries(this.metadata).filter(([, value]) => value !== '' && value !== undefined),
    );
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
      this.operationMessage =
        'The image could not be embedded. Use a PNG, JPEG, GIF, or WebP image under 10 MB.';
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
      const results = await this.library.moveTracks(
        [...this.selectedTrackIds()],
        this.destinationFolderId,
      );
      const succeeded = results.filter((result) => result.succeeded).length;
      this.operationMessage = `Moved ${succeeded} of ${results.length} selected tracks.`;
      await this.loadLibrary();
    } catch {
      this.operationMessage = 'The selected tracks could not be moved.';
    }
  }
}
