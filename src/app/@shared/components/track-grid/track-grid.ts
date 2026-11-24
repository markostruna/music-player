import { Component, effect, inject, input, signal } from '@angular/core';
import { MusicTrack } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { PlaylistService } from '@shared/services/playlist.service';

@Component({
  selector: 'app-track-grid',
  templateUrl: './track-grid.html',
  styleUrl: './track-grid.scss',
})
export class TrackGrid {
  private readonly player = inject(PlayerService);
  private readonly playlists = inject(PlaylistService);
  private loaded = false;

  readonly tracks = input.required<readonly MusicTrack[]>();
  readonly active = input(true);
  readonly currentTrack = this.player.currentTrack;
  readonly isPlaying = this.player.isPlaying;
  readonly playlistItems = this.playlists.playlists;
  readonly playlistError = signal('');
  readonly addingTrack = signal(false);

  constructor() {
    effect(() => {
      if (this.active() && !this.loaded) {
        this.loaded = true;
        void this.playlists.load().catch(() => this.playlistError.set('Playlists could not be loaded.'));
      }
    });
  }

  chooseTrack(track: MusicTrack): void {
    this.player.play([track], 0);
  }

  async addToPlaylist(track: MusicTrack, event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const id = Number(select.value);
    select.value = '';
    const playlist = this.playlistItems().find((item) => item.id === id);
    if (!playlist || this.addingTrack()) return;
    this.addingTrack.set(true);
    this.playlistError.set('');
    try {
      const current = await this.playlists.get(id);
      await this.playlists.update({ ...current, tracks: [...current.tracks, track] });
    } catch {
      this.playlistError.set('Track could not be added to the playlist.');
    } finally {
      this.addingTrack.set(false);
    }
  }
}
