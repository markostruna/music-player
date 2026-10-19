import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthState, ThemeName } from '@shared/services/auth-state';

@Component({
  selector: 'app-settings-page',
  imports: [RouterLink],
  templateUrl: './settings-page.html',
})
export class SettingsPage {
  private readonly auth = inject(AuthState);
  readonly user = this.auth.user;
  readonly isAdmin = this.auth.isAdmin;
  readonly themeOptions: ThemeName[] = ['Light', 'Dark', 'Blue'];
  readonly errorMessage = signal('');

  async changeTheme(event: Event): Promise<void> {
    try {
      await this.auth.setTheme((event.target as HTMLSelectElement).value as ThemeName);
      this.errorMessage.set('');
    } catch {
      this.errorMessage.set('The theme preference could not be saved.');
    }
  }
}
