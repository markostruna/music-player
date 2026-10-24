import { inject, Injectable, signal } from '@angular/core';
import { MusicLibraryApi, MusicTrack } from './music-library-api';

export type LibraryView = 'tracks' | 'albums' | 'artists';
export const LIBRARY_PAGE_SIZE = 100;

// Keeps the loaded library and browsing state so returning to the library page does not reload it.
@Injectable({ providedIn: 'root' })
export class LibraryStore {
  private readonly api = inject(MusicLibraryApi);
  private loaded = false;
  private pending: Promise<void> | null = null;
  private pageRequestId = 0;

  readonly tracks = signal<MusicTrack[]>([]);
  readonly pageTracks = signal<MusicTrack[]>([]);
  readonly totalTrackCount = signal(0);
  readonly isLoading = signal(true);
  readonly errorMessage = signal('');
  readonly collectionView = signal<LibraryView>('albums');
  readonly searchQuery = signal('');
  readonly currentPage = signal(1);
  scrollTop = 0;

  ensureLoaded(): Promise<void> {
    return this.loaded ? Promise.resolve() : this.refresh();
  }

  refresh(): Promise<void> {
    if (this.collectionView() === 'tracks') {
      return this.loadTrackPage(this.searchQuery(), this.currentPage(), LIBRARY_PAGE_SIZE);
    }
    this.pending ??= this.load().finally(() => (this.pending = null));
    return this.pending;
  }

  async loadTrackPage(query: string, page: number, pageSize: number): Promise<void> {
    const requestId = ++this.pageRequestId;
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      const result = await this.api.listTrackPage(query, page, pageSize);
      if (requestId !== this.pageRequestId) return;
      this.pageTracks.set(result.items);
      this.totalTrackCount.set(result.totalCount);
      this.currentPage.set(result.page);
    } catch {
      if (requestId === this.pageRequestId) {
        this.errorMessage.set('The library could not be loaded. Check the music server connection.');
      }
    } finally {
      if (requestId === this.pageRequestId) this.isLoading.set(false);
    }
  }

  private async load(): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      this.tracks.set(await this.api.listTracks());
      this.loaded = true;
    } catch {
      this.errorMessage.set('The library could not be loaded. Check the music server connection.');
    } finally {
      this.isLoading.set(false);
    }
  }

  invalidate(): void {
    this.loaded = false;
  }

  clear(): void {
    this.loaded = false;
    this.pageRequestId++;
    this.tracks.set([]);
    this.pageTracks.set([]);
    this.totalTrackCount.set(0);
    this.searchQuery.set('');
    this.currentPage.set(1);
    this.collectionView.set('albums');
    this.scrollTop = 0;
  }
}
