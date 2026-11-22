import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { BUILT_IN_PLAYLISTS, BuiltInPlaylistId, PLAYLIST_BACKGROUNDS, playlistBackgroundUrl, Playlist, PlaylistService } from '@shared/services/playlist.service';
import { PlayerService } from '@shared/services/player.service';

@Component({
  selector: 'app-playlist-detail',
  imports: [FormsModule, RouterLink],
  templateUrl: './playlist-detail.html',
  styleUrl: '../settings-page/settings-page.scss',
})
export class PlaylistDetail implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly service = inject(PlaylistService);
  private readonly player = inject(PlayerService);
  readonly playlist = signal<Playlist | null>(null);
  readonly builtIn = BUILT_IN_PLAYLISTS.find((item) => item.id === this.route.snapshot.paramMap.get('id'));
  readonly backgrounds = PLAYLIST_BACKGROUNDS;
  readonly playlistBackgroundUrl = playlistBackgroundUrl;
  readonly error = signal('');
  readonly busy = signal(false);
  name = '';

  ngOnInit(): void {
    if (this.builtIn) return;
    const raw = this.route.snapshot.paramMap.get('id') ?? '';
    if (!/^[1-9]\d*$/.test(raw)) {
      this.error.set('Playlist not found.');
      return;
    }
    void this.service.get(Number(raw)).then((playlist) => {
      this.playlist.set(playlist);
      this.name = playlist.name;
    }).catch(() => this.error.set('Playlist could not be loaded.'));
  }

  async playBuiltIn(id: BuiltInPlaylistId): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.player.playBuiltIn(id);
    } catch {
      this.error.set('Random playback could not be started. Check the music library and server connection.');
    } finally {
      this.busy.set(false);
    }
  }

  play(): void {
    const tracks = this.playlist()?.tracks ?? [];
    if (tracks.length) this.player.play(tracks, 0);
  }

  async save(tracks = this.playlist()?.tracks ?? []): Promise<void> {
    const playlist = this.playlist();
    if (!playlist || !this.name.trim() || this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      this.playlist.set(await this.service.update({ ...playlist, name: this.name.trim(), tracks }));
    } catch {
      this.error.set('Playlist could not be saved.');
    } finally {
      this.busy.set(false);
    }
  }

  async chooseBackground(backgroundId: number): Promise<void> {
    const playlist = this.playlist();
    if (!playlist || this.busy() || playlist.backgroundId === backgroundId) return;
    this.busy.set(true);
    this.error.set('');
    try {
      this.playlist.set(await this.service.update({ ...playlist, backgroundId }));
    } catch {
      this.error.set('Playlist background could not be saved.');
    } finally {
      this.busy.set(false);
    }
  }

  move(index: number, offset: number): void {
    const tracks = [...(this.playlist()?.tracks ?? [])];
    const target = index + offset;
    if (target < 0 || target >= tracks.length) return;
    [tracks[index], tracks[target]] = [tracks[target], tracks[index]];
    void this.save(tracks);
  }

  remove(index: number): void {
    void this.save((this.playlist()?.tracks ?? []).filter((_, i) => i !== index));
  }

  async delete(): Promise<void> {
    const playlist = this.playlist();
    if (!playlist || this.busy() || !confirm(`Delete "${playlist.name}"?`)) return;
    this.busy.set(true);
    try {
      await this.service.delete(playlist.id);
      await this.router.navigate(['/playlists']);
    } catch {
      this.error.set('Playlist could not be deleted.');
      this.busy.set(false);
    }
  }
}
