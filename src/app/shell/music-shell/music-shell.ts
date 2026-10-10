import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { Location } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  ActivatedRoute,
  NavigationEnd,
  Router,
  RouterOutlet,
} from '@angular/router';
import { filter } from 'rxjs';
import { AuthState, ThemeName } from '@shared/services/auth-state';
import { LibraryStore } from '@shared/services/library-store';
import { PlayerService } from '@shared/services/player.service';
import { ShellHeader } from '../header/shell-header';
import { MusicShellState } from './music-shell-state';
import { SideMenu } from '../side-menu/side-menu';

@Component({
  selector: 'app-music-shell',
  imports: [RouterOutlet, SideMenu, ShellHeader],
  templateUrl: './music-shell.html',
})
export class MusicShell {
  private readonly location = inject(Location);
  private readonly auth = inject(AuthState);
  private readonly library = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly shellState = inject(MusicShellState);
  private readonly navigationEnd = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
    ),
    { initialValue: null },
  );

  readonly activeSection = this.shellState.activeSection;
  readonly isAdmin = this.auth.isAdmin;
  readonly user = this.auth.user;
  readonly menuVisible = signal(true);
  readonly themeOptions: ThemeName[] = ['Light', 'Dark', 'Blue'];
  readonly themeError = signal('');
  readonly searchQuery = this.library.searchQuery;
  readonly isLibraryPage = computed(() => {
    this.navigationEnd();
    return this.route.firstChild?.snapshot.routeConfig?.path === 'library';
  });
  readonly pageTitle = computed(() => {
    this.navigationEnd();
    const childRoute = this.route.firstChild;
    switch (childRoute?.snapshot.routeConfig?.path) {
      case 'library':
        return this.activeSection();
      case 'library/album/:trackId': {
        const trackId = Number(childRoute.snapshot.paramMap.get('trackId'));
        return this.library.tracks().find((track) => track.id === trackId)?.album ?? 'Album';
      }
      case 'library/artist/:name': {
        const name = childRoute.snapshot.paramMap.get('name') ?? '';
        const normalizedName = name.trim().toLowerCase();
        const track = this.library
          .tracks()
          .find(
            (candidate) =>
              (candidate.artist.trim() || 'Unknown artist').toLowerCase() === normalizedName ||
              (candidate.albumArtist.trim() || candidate.artist).toLowerCase() === normalizedName,
          );
        return (
          [track?.artist, track?.albumArtist]
            .find((artist) => artist?.trim().toLowerCase() === normalizedName)
            ?.trim() ||
          name ||
          'Artist'
        );
      }
      default:
        return 'Now playing';
    }
  });

  goBack(): void {
    this.location.back();
  }

  constructor() {
    if (typeof window !== 'undefined' && window.matchMedia) {
      const mobile = window.matchMedia('(max-width: 680px)');
      const showMenuOnMobile = () => {
        if (mobile.matches) {
          this.menuVisible.set(true);
        }
      };
      showMenuOnMobile();
      mobile.addEventListener('change', showMenuOnMobile);
      inject(DestroyRef).onDestroy(() => mobile.removeEventListener('change', showMenuOnMobile));
    }

    effect(() => {
      this.navigationEnd();
      this.activeSection.set('Your library');
    });
  }

  setSearchQuery(query: string): void {
    this.searchQuery.set(query);
    this.library.currentPage.set(1);
  }

  toggleMenu(): void {
    this.menuVisible.update((visible) => !visible);
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
    this.library.clear();
    await this.auth.signOut();
    void this.router.navigateByUrl('/login');
  }
}
