import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LIBRARY_PAGE_SIZE, LibraryStore } from '@shared/services/library-store';

@Component({
  selector: 'app-library-artists-page',
  imports: [RouterLink],
  templateUrl: './artists-page.html',
})
export class ArtistsPage {
  private readonly store = inject(LibraryStore);

  readonly active = input(false);
  readonly refreshTrigger = input(0);
  readonly searchQuery = this.store.searchQuery;
  readonly currentPage = signal(1);
  readonly pageSize = LIBRARY_PAGE_SIZE;
  readonly isLoading = computed(() => this.store.pageLoading().artists);
  readonly visibleArtists = this.store.pageArtists;
  readonly totalItems = this.store.totalArtistCount;
  readonly errorMessage = computed(() => this.store.pageErrors().artists);
  readonly hasMore = computed(() => this.visibleArtists().length < this.totalItems());
  private loaded = false;
  private lastRefreshTrigger = 0;
  private lastSearchQuery = '';

  constructor() {
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
        void this.loadPage();
      } else if (active && query !== this.lastSearchQuery) {
        this.lastSearchQuery = query;
        this.currentPage.set(1);
        void this.loadPage();
      }
    });
  }

  async loadPage(): Promise<void> {
    await this.store.loadCollectionPage(
      'artists',
      this.searchQuery(),
      this.currentPage(),
      this.pageSize,
    );
    this.currentPage.set(this.store.currentPages().artists);
  }

  async loadMore(): Promise<void> {
    const page = this.currentPage() + 1;
    await this.store.loadCollectionPage('artists', this.searchQuery(), page, this.pageSize, true);
    this.currentPage.set(this.store.currentPages().artists);
  }
}
