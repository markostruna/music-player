import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { LIBRARY_PAGE_SIZE, LibraryStore } from '@shared/services/library-store';
import { MusicTrack, TrackFolderScope } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';

@Component({
  selector: 'app-library-tracks-page',
  templateUrl: './tracks-page.html',
  styleUrl: './tracks-page.scss',
})
export class TracksPage {
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);

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
  readonly totalItems = this.store.totalTrackCount;
  readonly visibleTracks = computed(() => this.pageTracks());
  readonly hasMore = computed(() => this.visibleTracks().length < this.totalItems());
  readonly errorMessage = computed(() => this.store.pageErrors().tracks);

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
    await this.store.loadTrackPage(
      this.searchQuery(),
      this.currentPage(),
      this.pageSize,
      this.folderScope(),
    );
    this.currentPage.set(this.store.currentPages().tracks);
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

  chooseTrack(track: MusicTrack): void {
    this.player.play([track], 0);
  }

  private folderScope(): TrackFolderScope | null {
    const sourceRootId = this.folderSourceRootId();
    const path = this.folderPath();
    return sourceRootId === null || path === null ? null : { sourceRootId, path };
  }
}
