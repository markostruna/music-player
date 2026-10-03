import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthState, ThemeName } from './auth-state';
import { LibraryStore } from './library-store';
import { PlayerService } from './player.service';

@Component({
  selector: 'app-now-playing-page',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './now-playing-page.html',
})
export class NowPlayingPage {
  private readonly auth = inject(AuthState);
  private readonly router = inject(Router);
  private readonly store = inject(LibraryStore);
  protected readonly player = inject(PlayerService);
  protected readonly track = this.player.currentTrack;
  protected readonly year = computed(() => (this.track()?.year ?? 0) > 0 ? this.track()!.year : null);
  protected readonly isAdmin = this.auth.isAdmin;
  protected readonly user = this.auth.user;
  protected readonly themeOptions: ThemeName[] = ['Light', 'Dark', 'Blue'];
  protected readonly themeError = signal('');

  protected async changeTheme(event: Event): Promise<void> {
    try {
      await this.auth.setTheme((event.target as HTMLSelectElement).value as ThemeName);
      this.themeError.set('');
    } catch {
      this.themeError.set('The theme preference could not be saved.');
    }
  }

  protected async signOut(): Promise<void> {
    this.player.stop();
    this.store.clear();
    await this.auth.signOut();
    void this.router.navigateByUrl('/login');
  }
}
