import { inject, Injectable, signal } from '@angular/core';
import {
  AlbumSummary,
  ArtistSummary,
  MusicLibraryApi,
  MusicTrack,
} from './music-library-api';

export type LibraryView = 'tracks' | 'albums' | 'artists';
export const LIBRARY_PAGE_SIZE = 100;

// Keeps the loaded library and browsing state so returning to the library page does not reload it.
@Injectable({ providedIn: 'root' })
export class LibraryStore {
  private readonly api = inject(MusicLibraryApi);
  private loaded = false;
  private pendingTracks: Promise<void> | null = null;
  private pageRequestId = 0;

  readonly tracks = signal<MusicTrack[]>([]);
  readonly pageTracks = signal<MusicTrack[]>([]);
  readonly totalTrackCount = signal(0);
  readonly pageAlbums = signal<AlbumSummary[]>([]);
  readonly totalAlbumCount = signal(0);
  readonly pageArtists = signal<ArtistSummary[]>([]);
  readonly totalArtistCount = signal(0);
  readonly isLoading = signal(true);
  readonly errorMessage = signal('');
  readonly collectionView = signal<LibraryView>('albums');
  readonly searchQuery = signal('');
  readonly currentPage = signal(1);
  scrollTop = 0;

  ensureLoaded(): Promise<void> {
    if (this.loaded) return Promise.resolve();
    this.pendingTracks ??= this.loadTracks().finally(() => (this.pendingTracks = null));
    return this.pendingTracks;
  }

  refresh(): Promise<void> {
    const view = this.collectionView();
    if (view === 'tracks') {
      return this.loadTrackPage(this.searchQuery(), this.currentPage(), LIBRARY_PAGE_SIZE);
    }
    return this.loadCollectionPage(
      view,
      this.searchQuery(),
      this.currentPage(),
      LIBRARY_PAGE_SIZE,
    );
  }

  refreshTracks(): Promise<void> {
    this.loaded = false;
    this.pendingTracks ??= this.loadTracks().finally(() => (this.pendingTracks = null));
    return this.pendingTracks;
  }

  async loadCollectionPage(
    view: Exclude<LibraryView, 'tracks'>,
    query: string,
    page: number,
    pageSize: number,
  ): Promise<void> {
    const requestId = ++this.pageRequestId;
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      if (view === 'albums') {
        const result = await this.api.listAlbumPage(query, page, pageSize);
        if (requestId !== this.pageRequestId) return;
        this.pageAlbums.set(result.items);
        this.totalAlbumCount.set(result.totalCount);
        this.currentPage.set(result.page);
      } else {
        const result = await this.api.listArtistPage(query, page, pageSize);
        if (requestId !== this.pageRequestId) return;
        this.pageArtists.set(result.items);
        this.totalArtistCount.set(result.totalCount);
        this.currentPage.set(result.page);
      }
    } catch {
      if (requestId === this.pageRequestId) {
        this.errorMessage.set('The library could not be loaded. Check the music server connection.');
      }
    } finally {
      if (requestId === this.pageRequestId) this.isLoading.set(false);
    }
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

  private async loadTracks(): Promise<void> {
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
    this.pageAlbums.set([]);
    this.totalAlbumCount.set(0);
    this.pageArtists.set([]);
    this.totalArtistCount.set(0);
    this.searchQuery.set('');
    this.currentPage.set(1);
    this.collectionView.set('albums');
    this.scrollTop = 0;
  }
}
