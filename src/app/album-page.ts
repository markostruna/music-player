import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthState, ThemeName } from './auth-state';
import { formatTime } from './format-time';
import { LibraryStore } from './library-store';
import { MusicTrack } from './music-library-api';
import { PlayerService } from './player.service';

@Component({
  selector: 'app-album-page',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './album-page.html',
})
export class AlbumPage implements OnInit {
  private readonly auth = inject(AuthState);
  private readonly router = inject(Router);
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  private readonly params = toSignal(inject(ActivatedRoute).paramMap);

  readonly isAdmin = this.auth.isAdmin;
  readonly user = this.auth.user;
  readonly themeOptions: ThemeName[] = ['Light', 'Dark', 'Blue'];
  readonly themeError = signal('');
  readonly isLoading = this.store.isLoading;
  readonly errorMessage = computed(() => {
    if (this.store.errorMessage()) return this.store.errorMessage();
    return !this.store.isLoading() && !this.album() ? 'This album could not be found in your library.' : '';
  });
  readonly currentTrack = this.player.currentTrack;
  readonly isPlaying = this.player.isPlaying;
  readonly formatTime = formatTime;
  readonly album = computed(() => {
    const trackId = Number(this.params()?.get('trackId'));
    const selectedTrack = this.store.tracks().find((track) => track.id === trackId);
    if (!selectedTrack) return null;

    const artist = selectedTrack.albumArtist.trim() || selectedTrack.artist;
    const albumTracks = this.store.tracks()
      .filter((track) =>
        track.album.toLowerCase() === selectedTrack.album.toLowerCase()
        && (track.albumArtist.trim() || track.artist).toLowerCase() === artist.toLowerCase())
      .sort((first, second) =>
        first.discNumber - second.discNumber
        || first.trackNumber - second.trackNumber
        || first.title.localeCompare(second.title));

    return {
      title: selectedTrack.album,
      artist,
      coverUrl: selectedTrack.coverUrl,
      releaseYear: albumTracks.find((track) => track.year > 0)?.year,
      tracks: albumTracks,
    };
  });

  ngOnInit(): void {
    void this.store.ensureLoaded();
  }

  playTrack(tracks: MusicTrack[], index: number): void {
    this.player.play(tracks, index);
  }

  async changeTheme(event: Event): Promise<void> {
    try {
      await this.auth.setTheme((event.target as HTMLSelectElement).value as ThemeName);
      this.themeError.set('');
    } catch {
      this.themeError.set('The theme preference could not be saved.');
    }
  }

  async signOut(): Promise<void> {
    this.player.stop();
    this.store.clear();
    await this.auth.signOut();
    void this.router.navigateByUrl('/login');
  }
}
