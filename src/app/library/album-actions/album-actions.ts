import { inject, Injectable, signal } from '@angular/core';
import { LibraryStore } from '@shared/services/library-store';

export interface AlbumRef {
  artist: string;
  title: string;
}

export type AlbumAction = 'refresh' | 'cover' | 'tags' | 'move';

export type ArtistAction = 'refresh' | 'cover';

const MENU_WIDTH = 190;
const MENU_HEIGHT = 230;
const ARTIST_MENU_HEIGHT = 160;

// Shared state for the album tile menu and its dialogs; rendered once by AlbumActionsHost.
@Injectable({ providedIn: 'root' })
export class AlbumActions {
  private readonly store = inject(LibraryStore);
  private dirty = false;

  readonly menu = signal<{ album: AlbumRef; left: number; top: number } | null>(null);
  readonly dialog = signal<{ action: AlbumAction; album: AlbumRef } | null>(null);
  readonly artistMenu = signal<{ artist: string; left: number; top: number } | null>(null);
  readonly artistDialog = signal<{ action: ArtistAction; artist: string } | null>(null);
  // Incremented whenever an album was changed so pages can reload their data.
  readonly changeCount = signal(0);

  isMenuOpen(album: AlbumRef): boolean {
    const menu = this.menu();
    return !!menu && this.sameAlbum(menu.album, album);
  }

  openMenu(album: AlbumRef, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (this.isMenuOpen(album)) {
      this.closeMenu();
      return;
    }
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    this.menu.set({
      album,
      left: Math.max(8, Math.min(rect.left, window.innerWidth - MENU_WIDTH - 8)),
      top:
        rect.bottom + MENU_HEIGHT > window.innerHeight
          ? Math.max(8, rect.top - MENU_HEIGHT)
          : rect.bottom + 4,
    });
  }

  closeMenu(): void {
    this.menu.set(null);
    this.artistMenu.set(null);
  }

  isArtistMenuOpen(artist: string): boolean {
    return this.artistMenu()?.artist === artist;
  }

  openArtistMenu(artist: string, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (this.isArtistMenuOpen(artist)) {
      this.closeMenu();
      return;
    }
    this.menu.set(null);
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    this.artistMenu.set({
      artist,
      left: Math.max(8, Math.min(rect.left, window.innerWidth - MENU_WIDTH - 8)),
      top:
        rect.bottom + ARTIST_MENU_HEIGHT > window.innerHeight
          ? Math.max(8, rect.top - ARTIST_MENU_HEIGHT)
          : rect.bottom + 4,
    });
  }

  openArtistDialog(action: ArtistAction, artist: string): void {
    this.closeMenu();
    this.dirty = false;
    this.artistDialog.set({ action, artist });
  }

  closeArtistDialog(changed = false): void {
    const hasChanges = changed || this.dirty;
    this.dirty = false;
    this.artistDialog.set(null);
    if (!hasChanges) return;
    void this.store.refreshTracks();
    this.changeCount.update((count) => count + 1);
  }

  openDialog(action: AlbumAction, album: AlbumRef): void {
    this.closeMenu();
    this.dirty = false;
    this.dialog.set({ action, album });
  }

  markChanged(): void {
    this.dirty = true;
  }

  closeDialog(changed = false): void {
    const hasChanges = changed || this.dirty;
    this.dirty = false;
    this.dialog.set(null);
    if (!hasChanges) return;
    void this.store.refreshTracks();
    this.changeCount.update((count) => count + 1);
  }

  private sameAlbum(first: AlbumRef, second: AlbumRef): boolean {
    return first.artist === second.artist && first.title === second.title;
  }
}
