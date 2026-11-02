import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthState } from '@shared/services/auth-state';
import { LIBRARY_PAGE_SIZE, LibraryStore } from '@shared/services/library-store';
import {
  MetadataPatch,
  MusicLibraryApi,
  MusicTrack,
  TrackFolderScope,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { ToastService } from '@shared/services/toast-service';
import { formatTime } from '@shared/utils/format-time';

@Component({
  selector: 'app-library-tracks-page',
  imports: [FormsModule],
  templateUrl: './tracks-page.html',
  styleUrl: './tracks-page.scss',
})
export class TracksPage {
  private readonly auth = inject(AuthState);
  private readonly library = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  private readonly toast = inject(ToastService);

  readonly active = input(false);
  readonly refreshTrigger = input(0);
  readonly folderSourceRootId = input<number | null>(null);
  readonly folderPath = input<string | null>(null);
  readonly searchQuery = this.store.searchQuery;
  readonly currentPage = signal(1);
  readonly pageTracks = this.store.pageTracks;
  readonly pageSize = LIBRARY_PAGE_SIZE;
  readonly isPlaying = this.player.isPlaying;
  readonly isLoading = computed(() => this.store.pageLoading().tracks);
  readonly currentTrack = this.player.currentTrack;
  readonly isAdmin = this.auth.isAdmin;
  readonly selectedTrackIds = signal(new Set<number>());
  readonly hasSelection = computed(() => this.selectedTrackIds().size > 0);
  readonly totalItems = this.store.totalTrackCount;
  readonly visibleTracks = computed(() => this.pageTracks());
  readonly hasMore = computed(() => this.visibleTracks().length < this.totalItems());
  readonly errorMessage = computed(() => this.store.pageErrors().tracks || this.pageError());
  readonly formatTime = formatTime;

  private readonly pageError = signal('');
  metadata: MetadataPatch = {};
  destinationFolderId: number | null = null;
  folders: Awaited<ReturnType<MusicLibraryApi['listTrackMoveFolders']>> = [];
  operationMessage = '';
  private loaded = false;
  private lastRefreshTrigger = 0;
  private lastSearchQuery = '';
  private lastFolderSourceRootId: number | null = null;
  private lastFolderPath: string | null = null;

  constructor() {
    effect(() => {
      const active = this.active();
      const refreshTrigger = this.refreshTrigger();
      const query = this.searchQuery();
      const folderSourceRootId = this.folderSourceRootId();
      const folderPath = this.folderPath();
      if (refreshTrigger !== this.lastRefreshTrigger) {
        this.lastRefreshTrigger = refreshTrigger;
        if (active && this.loaded) {
          this.lastSearchQuery = query;
          this.lastFolderSourceRootId = folderSourceRootId;
          this.lastFolderPath = folderPath;
          this.currentPage.set(1);
          void this.loadLibrary();
        }
        return;
      }
      if (active && !this.loaded) {
        this.loaded = true;
        this.lastSearchQuery = query;
        this.lastFolderSourceRootId = folderSourceRootId;
        this.lastFolderPath = folderPath;
        this.selectedTrackIds.set(new Set<number>());
        void this.loadLibrary();
      } else if (
        active &&
        (query !== this.lastSearchQuery ||
          folderSourceRootId !== this.lastFolderSourceRootId ||
          folderPath !== this.lastFolderPath)
      ) {
        this.lastSearchQuery = query;
        this.lastFolderSourceRootId = folderSourceRootId;
        this.lastFolderPath = folderPath;
        this.currentPage.set(1);
        void this.loadLibrary();
      }
    });
  }

  async loadLibrary(): Promise<void> {
    this.pageError.set('');
    await this.store.loadTrackPage(
      this.searchQuery(),
      this.currentPage(),
      this.pageSize,
      this.folderScope(),
    );
    this.currentPage.set(this.store.currentPages().tracks);
    if (this.isAdmin() && !this.store.errorMessage()) {
      try {
        this.folders = await this.library.listTrackMoveFolders();
      } catch {
        this.pageError.set('The music folders could not be loaded.');
      }
    }
  }

  async loadMore(): Promise<void> {
    const page = this.currentPage() + 1;
    await this.store.loadTrackPage(
      this.searchQuery(),
      page,
      this.pageSize,
      this.folderScope(),
      true,
    );
    this.currentPage.set(this.store.currentPages().tracks);
  }

  async chooseTrack(track: MusicTrack): Promise<void> {
    try {
      const queue = await this.library.searchTracks(this.searchQuery(), this.folderScope());
      this.player.play(queue, queue.findIndex((candidate) => candidate.id === track.id));
    } catch {
      this.toast.show('The selected track could not be loaded for playback.', 'error');
    }
  }

  private folderScope(): TrackFolderScope | null {
    const sourceRootId = this.folderSourceRootId();
    const path = this.folderPath();
    return sourceRootId === null || path === null ? null : { sourceRootId, path };
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
