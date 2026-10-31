import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthState, ThemeName } from '@shared/services/auth-state';
import { AlbumIdentifyApi, IdentifyProvider } from '@shared/services/album-identify-api';
import { ProviderSettingsApi } from '@shared/services/provider-settings-api';

@Component({
  selector: 'app-settings-page',
  imports: [FormsModule, RouterLink],
  templateUrl: './settings-page.html',
  styleUrl: './settings-page.scss',
})
export class SettingsPage implements OnInit {
  private readonly auth = inject(AuthState);
  private readonly identifyApi = inject(AlbumIdentifyApi);
  private readonly providerSettingsApi = inject(ProviderSettingsApi);
  readonly user = this.auth.user;
  readonly isAdmin = this.auth.isAdmin;
  readonly themeOptions: ThemeName[] = ['Light', 'Dark', 'Blue'];
  readonly providers = signal<IdentifyProvider[]>([]);
  readonly defaultProviderId = signal('');
  readonly isSavingDefaultProvider = signal(false);
  readonly errorMessage = signal('');
  readonly statusMessage = signal('');

  ngOnInit(): void {
    if (this.isAdmin()) void this.loadDefaultProvider();
  }

  async changeTheme(event: Event): Promise<void> {
    try {
      await this.auth.setTheme((event.target as HTMLSelectElement).value as ThemeName);
      this.errorMessage.set('');
    } catch {
      this.errorMessage.set('The theme preference could not be saved.');
    }
  }

  async changeDefaultProvider(event: Event): Promise<void> {
    const previousProviderId = this.defaultProviderId();
    const providerId = (event.target as HTMLSelectElement).value;
    this.defaultProviderId.set(providerId);
    this.isSavingDefaultProvider.set(true);
    this.errorMessage.set('');
    this.statusMessage.set('');
    try {
      const saved = await this.providerSettingsApi.updateDefaultAlbumProvider(providerId);
      this.defaultProviderId.set(saved.providerId);
      this.statusMessage.set('Default metadata provider saved.');
    } catch {
      this.defaultProviderId.set(previousProviderId);
      this.errorMessage.set('The default metadata provider could not be saved.');
    } finally {
      this.isSavingDefaultProvider.set(false);
    }
  }

  private async loadDefaultProvider(): Promise<void> {
    try {
      const [providers, setting] = await Promise.all([
        this.identifyApi.listProviders(),
        this.providerSettingsApi.getDefaultAlbumProvider(),
      ]);
      this.providers.set(providers.filter((provider) => provider.isConfigured));
      this.defaultProviderId.set(setting.providerId);
    } catch {
      this.errorMessage.set('Metadata provider settings could not be loaded.');
    }
  }
}
