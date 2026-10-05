import { inject, Injectable, signal } from '@angular/core';
import { MusicLibraryApi, MusicTrack } from './music-library-api';

export type LibraryView = 'tracks' | 'albums' | 'artists';

// Keeps the loaded library and browsing state so returning to the library page does not reload it.
@Injectable({ providedIn: 'root' })
export class LibraryStore {
  private readonly api = inject(MusicLibraryApi);
  private loaded = false;
  private pending: Promise<void> | null = null;

  readonly tracks = signal<MusicTrack[]>([]);
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
    this.pending ??= this.load().finally(() => (this.pending = null));
    return this.pending;
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
    this.tracks.set([]);
    this.searchQuery.set('');
    this.currentPage.set(1);
    this.collectionView.set('albums');
    this.scrollTop = 0;
  }
}
