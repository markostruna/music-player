import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthState, ThemeName } from './auth-state';
import { LibraryStore } from './library-store';
import { MusicTrack } from './music-library-api';
import { PlayerService } from './player.service';

@Component({
  selector: 'app-artist-page',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './artist-page.html',
})
export class ArtistPage implements OnInit {
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
    return !this.store.isLoading() && !this.artist() ? 'This artist could not be found in your library.' : '';
  });
  readonly artist = computed(() => {
    const name = (this.params()?.get('name') ?? '').trim().toLowerCase();
    const tracks = this.store.tracks().filter((track) =>
      (track.artist.trim() || 'Unknown artist').toLowerCase() === name
      || (track.albumArtist.trim() || track.artist).toLowerCase() === name);
    if (!tracks.length) return null;

    const groups = new Map<string, { key: string; title: string; coverUrl: string; year: number; tracks: MusicTrack[] }>();
    for (const track of tracks) {
      const key = `${(track.albumArtist.trim() || track.artist).toLowerCase()}\u0000${track.album.toLowerCase()}`;
      const group = groups.get(key);
      if (group) {
        group.tracks.push(track);
        group.year ||= track.year;
      } else {
        groups.set(key, { key, title: track.album, coverUrl: track.coverUrl, year: track.year, tracks: [track] });
      }
    }

    const first = tracks[0];
    const displayName = [first.artist, first.albumArtist].find((value) => value.trim().toLowerCase() === name)?.trim() ?? name;
    return {
      name: displayName,
      coverUrl: first.coverUrl,
      trackCount: tracks.length,
      albums: [...groups.values()].sort((a, b) => b.year - a.year || a.title.localeCompare(b.title)),
    };
  });
  ngOnInit(): void {
    void this.store.ensureLoaded();
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
