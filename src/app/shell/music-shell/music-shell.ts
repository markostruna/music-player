import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { Location } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AuthState } from '@shared/services/auth-state';
import { LibraryStore } from '@shared/services/library-store';
import { PlayerService } from '@shared/services/player.service';
import { ShellHeader } from '../header/shell-header';
import { MusicShellState } from './music-shell-state';
import { SideMenu } from '../side-menu/side-menu';
import { ToastMessageComponent } from '@shared/components/toast-message/toast-message';
import { LibraryOperationTracker } from '@shared/services/library-operation-tracker';

@Component({
  selector: 'app-music-shell',
  imports: [RouterOutlet, SideMenu, ShellHeader, ToastMessageComponent],
  templateUrl: './music-shell.html',
})
export class MusicShell {
  private readonly location = inject(Location);
  private readonly auth = inject(AuthState);
  private readonly library = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  readonly libraryOperations = inject(LibraryOperationTracker);
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
  readonly user = this.auth.user;
  readonly menuVisible = signal(true);
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
      case 'admin':
        return 'Manage library';
      case 'playlists':
        return 'Playlists';
      case 'settings':
        return 'Settings';
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
      const landscape = window.matchMedia('(orientation: landscape)');
      const updateMenuVisibility = () => {
        if (landscape.matches) {
          this.menuVisible.set(false);
        } else if (mobile.matches) {
          this.menuVisible.set(true);
        }
      };
      updateMenuVisibility();
      mobile.addEventListener('change', updateMenuVisibility);
      landscape.addEventListener('change', updateMenuVisibility);
      inject(DestroyRef).onDestroy(() => {
        mobile.removeEventListener('change', updateMenuVisibility);
        landscape.removeEventListener('change', updateMenuVisibility);
      });
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

  async signOut(): Promise<void> {
    this.player.stop();
    this.library.clear();
    await this.auth.signOut();
    void this.router.navigateByUrl('/login');
  }
}
