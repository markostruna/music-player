import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { AlbumTile } from '@shared/components/album-tile/album-tile';
import { LIBRARY_PAGE_SIZE, LibraryStore } from '@shared/services/library-store';
import { AlbumActions } from '../../album-actions/album-actions';

@Component({
  selector: 'app-library-albums-page',
  imports: [AlbumTile],
  templateUrl: './albums-page.html',
})
export class AlbumsPage {
  private readonly store = inject(LibraryStore);
  readonly albumActions = inject(AlbumActions);

  readonly active = input(false);
  readonly refreshTrigger = input(0);
  readonly searchQuery = this.store.searchQuery;
  readonly currentPage = signal(1);
  readonly pageSize = LIBRARY_PAGE_SIZE;
  readonly isLoading = computed(() => this.store.pageLoading().albums);
  readonly visibleAlbums = this.store.pageAlbums;
  readonly totalItems = this.store.totalAlbumCount;
  readonly errorMessage = computed(() => this.store.pageErrors().albums);
  readonly hasMore = computed(() => this.visibleAlbums().length < this.totalItems());
  private loaded = false;
  private lastRefreshTrigger = 0;
  private lastSearchQuery = '';
  private lastChangeCount = this.albumActions.changeCount();

  constructor() {
    effect(() => {
      const changeCount = this.albumActions.changeCount();
      if (changeCount === this.lastChangeCount) return;
      this.lastChangeCount = changeCount;
      if (untracked(() => this.active() && this.loaded)) void untracked(() => this.loadPage());
    });
    effect(() => {
      const active = this.active();
      const refreshTrigger = this.refreshTrigger();
      const query = this.searchQuery();
      if (refreshTrigger !== this.lastRefreshTrigger) {
        this.lastRefreshTrigger = refreshTrigger;
        if (active && this.loaded) {
          this.lastSearchQuery = query;
          this.currentPage.set(1);
          void this.loadPage();
        }
        return;
      }
      if (active && !this.loaded) {
        this.loaded = true;
        this.lastSearchQuery = query;
        if (this.store.hasCachedCollectionPage('albums', query)) {
          this.currentPage.set(this.store.currentPages().albums);
        } else {
          void this.loadPage();
        }
      } else if (active && query !== this.lastSearchQuery) {
        this.lastSearchQuery = query;
        this.currentPage.set(1);
        void this.loadPage();
      }
    });
  }

  async loadPage(): Promise<void> {
    await this.store.loadCollectionPage(
      'albums',
      this.searchQuery(),
      this.currentPage(),
      this.pageSize,
    );
    this.currentPage.set(this.store.currentPages().albums);
  }

  async loadMore(): Promise<void> {
    const page = this.currentPage() + 1;
    await this.store.loadCollectionPage('albums', this.searchQuery(), page, this.pageSize, true);
    this.currentPage.set(this.store.currentPages().albums);
  }
}
