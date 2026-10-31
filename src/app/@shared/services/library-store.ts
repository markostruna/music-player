import { inject, Injectable, signal } from '@angular/core';
import {
  AlbumSummary,
  ArtistSummary,
  MusicLibraryApi,
  MusicTrack,
  TrackFolderScope,
} from './music-library-api';

export type LibraryView = 'tracks' | 'albums' | 'artists' | 'folders';
export const LIBRARY_PAGE_SIZE = 100;

// Keeps the loaded library and browsing state so returning to the library page does not reload it.
@Injectable({ providedIn: 'root' })
export class LibraryStore {
  private readonly api = inject(MusicLibraryApi);
  private loaded = false;
  private pendingTracks: Promise<void> | null = null;
  private pageRequestId = 0;
  private readonly pageRequestIds: Record<LibraryView, number> = {
    tracks: 0,
    albums: 0,
    artists: 0,
    folders: 0,
  };

  readonly tracks = signal<MusicTrack[]>([]);
  readonly pageTracks = signal<MusicTrack[]>([]);
  readonly totalTrackCount = signal(0);
  readonly pageAlbums = signal<AlbumSummary[]>([]);
  readonly totalAlbumCount = signal(0);
  readonly pageArtists = signal<ArtistSummary[]>([]);
  readonly totalArtistCount = signal(0);
  readonly pageLoading = signal<Record<LibraryView, boolean>>({
    tracks: false,
    albums: false,
    artists: false,
    folders: false,
  });
  readonly pageErrors = signal<Record<LibraryView, string>>({
    tracks: '',
    albums: '',
    artists: '',
    folders: '',
  });
  readonly currentPages = signal<Record<LibraryView, number>>({
    tracks: 1,
    albums: 1,
    artists: 1,
    folders: 1,
  });
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
    if (view === 'folders') return Promise.resolve();
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
    view: Exclude<LibraryView, 'tracks' | 'folders'>,
    query: string,
    page: number,
    pageSize: number,
  ): Promise<void> {
    const requestId = ++this.pageRequestIds[view];
    const latestRequestId = ++this.pageRequestId;
    this.pageLoading.update((loading) => ({ ...loading, [view]: true }));
    this.pageErrors.update((errors) => ({ ...errors, [view]: '' }));
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      if (view === 'albums') {
        const result = await this.api.listAlbumPage(query, page, pageSize);
        if (requestId !== this.pageRequestIds[view]) return;
        this.pageAlbums.set(result.items);
        this.totalAlbumCount.set(result.totalCount);
        this.currentPages.update((pages) => ({ ...pages, [view]: result.page }));
      } else {
        const result = await this.api.listArtistPage(query, page, pageSize);
        if (requestId !== this.pageRequestIds[view]) return;
        this.pageArtists.set(result.items);
        this.totalArtistCount.set(result.totalCount);
        this.currentPages.update((pages) => ({ ...pages, [view]: result.page }));
      }
      if (this.collectionView() === view) this.currentPage.set(this.currentPages()[view]);
    } catch {
      if (requestId === this.pageRequestIds[view]) {
        const message = 'The library could not be loaded. Check the music server connection.';
        this.pageErrors.update((errors) => ({ ...errors, [view]: message }));
        if (latestRequestId === this.pageRequestId) this.errorMessage.set(message);
      }
    } finally {
      if (requestId === this.pageRequestIds[view]) {
        this.pageLoading.update((loading) => ({ ...loading, [view]: false }));
      }
      if (latestRequestId === this.pageRequestId) this.isLoading.set(false);
    }
  }

  async loadTrackPage(
    query: string,
    page: number,
    pageSize: number,
    folderScope?: TrackFolderScope | null,
  ): Promise<void> {
    const requestId = ++this.pageRequestIds.tracks;
    const latestRequestId = ++this.pageRequestId;
    this.pageLoading.update((loading) => ({ ...loading, tracks: true }));
    this.pageErrors.update((errors) => ({ ...errors, tracks: '' }));
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      const result = await this.api.listTrackPage(query, page, pageSize, false, folderScope);
      if (requestId !== this.pageRequestIds.tracks) return;
      this.pageTracks.set(result.items);
      this.totalTrackCount.set(result.totalCount);
      this.currentPages.update((pages) => ({ ...pages, tracks: result.page }));
      if (this.collectionView() === 'tracks') this.currentPage.set(result.page);
    } catch {
      if (requestId === this.pageRequestIds.tracks) {
        const message = 'The library could not be loaded. Check the music server connection.';
        this.pageErrors.update((errors) => ({ ...errors, tracks: message }));
        if (latestRequestId === this.pageRequestId) this.errorMessage.set(message);
      }
    } finally {
      if (requestId === this.pageRequestIds.tracks) {
        this.pageLoading.update((loading) => ({ ...loading, tracks: false }));
      }
      if (latestRequestId === this.pageRequestId) this.isLoading.set(false);
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
    for (const view of Object.keys(this.pageRequestIds) as LibraryView[]) {
      this.pageRequestIds[view]++;
    }
    this.tracks.set([]);
    this.pageTracks.set([]);
    this.totalTrackCount.set(0);
    this.pageAlbums.set([]);
    this.totalAlbumCount.set(0);
    this.pageArtists.set([]);
    this.totalArtistCount.set(0);
    this.pageLoading.set({ tracks: false, albums: false, artists: false, folders: false });
    this.pageErrors.set({ tracks: '', albums: '', artists: '', folders: '' });
    this.currentPages.set({ tracks: 1, albums: 1, artists: 1, folders: 1 });
    this.searchQuery.set('');
    this.currentPage.set(1);
    this.collectionView.set('albums');
    this.scrollTop = 0;
  }
}
