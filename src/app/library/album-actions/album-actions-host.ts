import { Component, inject } from '@angular/core';
import { AuthState } from '@shared/services/auth-state';
import { MetadataRefreshResult, MusicLibraryApi } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { ToastService } from '@shared/services/toast-service';
import { MetadataRefreshDialog } from '../metadata-refresh-dialog/metadata-refresh-dialog';
import { AlbumActions } from './album-actions';
import { AlbumCoverDialog } from './album-cover-dialog';
import { AlbumMoveDialog } from './album-move-dialog';
import { AlbumTagsDialog } from './album-tags-dialog';
import { ArtistCoverDialog } from './artist-cover-dialog';

@Component({
  selector: 'app-album-actions-host',
  imports: [
    MetadataRefreshDialog,
    AlbumCoverDialog,
    AlbumTagsDialog,
    AlbumMoveDialog,
    ArtistCoverDialog,
  ],
  templateUrl: './album-actions-host.html',
})
export class AlbumActionsHost {
  private readonly library = inject(MusicLibraryApi);
  private readonly player = inject(PlayerService);
  private readonly toast = inject(ToastService);
  readonly actions = inject(AlbumActions);
  readonly isAdmin = inject(AuthState).isAdmin;

  async play(): Promise<void> {
    const album = this.actions.menu()?.album;
    this.actions.closeMenu();
    if (!album) return;
    try {
      this.player.play(await this.library.listAlbumTracks(album.artist, album.title), 0);
    } catch {
      this.toast.show('The album tracks could not be loaded.', 'error');
    }
  }

  async enqueue(): Promise<void> {
    const album = this.actions.menu()?.album;
    this.actions.closeMenu();
    if (!album) return;
    try {
      this.player.enqueue(await this.library.listAlbumTracks(album.artist, album.title));
      this.toast.show(`Added ${album.title} to the queue.`, 'success');
    } catch {
      this.toast.show('The album tracks could not be loaded.', 'error');
    }
  }

  async playArtist(): Promise<void> {
    const artist = this.actions.artistMenu()?.artist;
    this.actions.closeMenu();
    if (!artist) return;
    try {
      const tracks = await this.library.listArtistTracks(artist);
      if (!tracks.length) {
        this.toast.show(`No tracks were found for ${artist}.`, 'error');
        return;
      }
      this.player.play(tracks, 0);
    } catch {
      this.toast.show('The artist tracks could not be loaded.', 'error');
    }
  }

  async enqueueArtist(): Promise<void> {
    const artist = this.actions.artistMenu()?.artist;
    this.actions.closeMenu();
    if (!artist) return;
    try {
      const tracks = await this.library.listArtistTracks(artist);
      if (!tracks.length) {
        this.toast.show(`No tracks were found for ${artist}.`, 'error');
        return;
      }
      this.player.enqueue(tracks);
      this.toast.show(`Added ${artist} to the queue.`, 'success');
    } catch {
      this.toast.show('The artist tracks could not be loaded.', 'error');
    }
  }

  artistRefreshSaved(selection: { result: MetadataRefreshResult; providerId: string }): void {
    this.actions.markChanged();
    this.toast.show(`Artist information refreshed from ${selection.providerId}.`, 'success');
  }

  refreshSaved(selection: { result: MetadataRefreshResult; providerId: string }): void {
    this.actions.markChanged();
    this.toast.show(
      `Album information refreshed from ${selection.providerId} at ${new Date(selection.result.updatedAt).toLocaleString()}.`,
      'success',
    );
  }
}
