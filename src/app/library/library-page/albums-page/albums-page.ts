import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthState } from '@shared/services/auth-state';
import { LIBRARY_PAGE_SIZE, LibraryStore } from '@shared/services/library-store';
import { AlbumSummary, MusicLibraryApi } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { ToastService } from '@shared/services/toast-service';
import {
  IdentifyAlbumDialog,
  IdentifyAlbumRequest,
} from '../../identify-album-dialog/identify-album-dialog';

@Component({
  selector: 'app-library-albums-page',
  imports: [RouterLink, IdentifyAlbumDialog],
  templateUrl: './albums-page.html',
})
export class AlbumsPage {
  private readonly auth = inject(AuthState);
  private readonly library = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  private readonly toast = inject(ToastService);

  readonly identifyRequest = signal<IdentifyAlbumRequest | null>(null);
  readonly albumMenu = signal<{ album: AlbumSummary; left: number; top: number } | null>(null);
  readonly active = input(false);
  readonly refreshTrigger = input(0);
  readonly searchQuery = this.store.searchQuery;
  readonly currentPage = signal(1);
  readonly pageSize = LIBRARY_PAGE_SIZE;
  readonly isLoading = computed(() => this.store.pageLoading().albums);
  readonly isAdmin = this.auth.isAdmin;
  readonly visibleAlbums = this.store.pageAlbums;
  readonly totalItems = this.store.totalAlbumCount;
  readonly errorMessage = computed(() => this.store.pageErrors().albums);
  readonly hasMore = computed(() => this.visibleAlbums().length < this.totalItems());
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

  openAlbumMenu(album: AlbumSummary, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (this.albumMenu()?.album.coverTrackId === album.coverTrackId) {
      this.albumMenu.set(null);
      return;
    }
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const menuWidth = 160;
    const menuHeight = 130;
    this.albumMenu.set({
      album,
      left: Math.max(8, Math.min(rect.left, window.innerWidth - menuWidth - 8)),
      top:
        rect.bottom + menuHeight > window.innerHeight
          ? Math.max(8, rect.top - menuHeight)
          : rect.bottom + 4,
    });
  }

  closeAlbumMenu(): void {
    this.albumMenu.set(null);
  }

  async playAlbumFromMenu(): Promise<void> {
    const menu = this.albumMenu();
    this.closeAlbumMenu();
    if (!menu) return;
    try {
      this.player.play(await this.library.listAlbumTracks(menu.album.artist, menu.album.album), 0);
    } catch {
      this.toast.show('The album tracks could not be loaded.', 'error');
    }
  }

  async enqueueAlbumFromMenu(): Promise<void> {
    const menu = this.albumMenu();
    this.closeAlbumMenu();
    if (!menu) return;
    try {
      this.player.enqueue(await this.library.listAlbumTracks(menu.album.artist, menu.album.album));
      this.toast.show(`Added ${menu.album.album} to the queue.`, 'success');
    } catch {
      this.toast.show('The album tracks could not be loaded.', 'error');
    }
  }

  async identifyAlbumFromMenu(): Promise<void> {
    const menu = this.albumMenu();
    this.closeAlbumMenu();
    if (!menu) return;
    try {
      this.identifyRequest.set({
        title: menu.album.album,
        artist: menu.album.artist,
        tracks: await this.library.listAlbumTracks(menu.album.artist, menu.album.album),
      });
    } catch {
      this.toast.show('The album tracks could not be loaded.', 'error');
    }
  }

  async closeIdentify(changed: boolean): Promise<void> {
    this.identifyRequest.set(null);
    if (changed) await this.loadPage();
  }
}
