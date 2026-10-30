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
  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalItems() / this.pageSize)));
  readonly pageNumber = computed(() => Math.min(this.currentPage(), this.totalPages()));
  readonly pageStart = computed(() =>
    this.totalItems() === 0 ? 0 : (this.pageNumber() - 1) * this.pageSize + 1,
  );
  readonly pageEnd = computed(() => Math.min(this.pageNumber() * this.pageSize, this.totalItems()));
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
          this.currentPage.set(this.store.currentPages().artists);
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

  previousPage(): void {
    const page = Math.max(1, this.currentPage() - 1);
    void this.loadPageNumber(page);
  }

  nextPage(): void {
    const page = Math.min(this.totalPages(), this.currentPage() + 1);
    void this.loadPageNumber(page);
  }

  private async loadPageNumber(page: number): Promise<void> {
    await this.store.loadCollectionPage('artists', this.searchQuery(), page, this.pageSize);
    this.currentPage.set(this.store.currentPages().artists);
  }
}
