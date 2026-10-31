import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ProviderSettingsApi,
  ProviderSettingsItem,
} from '@shared/services/provider-settings-api';

@Component({
  selector: 'app-provider-settings-page',
  imports: [RouterLink],
  templateUrl: './provider-settings-page.html',
  styleUrl: './provider-settings-page.scss',
})
export class ProviderSettingsPage implements OnInit {
  private readonly api = inject(ProviderSettingsApi);

  readonly providers = signal<ProviderSettingsItem[]>([]);
  readonly drafts = signal<Record<string, Record<string, string>>>({});
  readonly isLoading = signal(true);
  readonly busyProvider = signal<string | null>(null);
  readonly errorMessage = signal('');
  readonly statusMessage = signal('');

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      this.providers.set(await this.api.list());
    } catch {
      this.errorMessage.set('Provider settings could not be loaded.');
    } finally {
      this.isLoading.set(false);
    }
  }

  setDraft(providerId: string, credentialKey: string, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.drafts.update((drafts) => ({
      ...drafts,
      [providerId]: { ...drafts[providerId], [credentialKey]: value },
    }));
  }

  async saveProvider(provider: ProviderSettingsItem): Promise<void> {
    const credentials = Object.fromEntries(
      Object.entries(this.drafts()[provider.id] ?? {}).filter(([, value]) => value.trim().length > 0),
    );
    if (Object.keys(credentials).length === 0) {
      this.errorMessage.set('Enter at least one credential before saving.');
      this.statusMessage.set('');
      return;
    }

    if (await this.updateProvider(provider, credentials, 'Provider credentials saved.')) {
      this.drafts.update((drafts) => ({ ...drafts, [provider.id]: {} }));
    }
  }

  async clearCredential(provider: ProviderSettingsItem, credentialKey: string): Promise<void> {
    if (await this.updateProvider(
      provider,
      { [credentialKey]: '' },
      'Provider credential removed.',
    )) {
      this.drafts.update((drafts) => ({
        ...drafts,
        [provider.id]: { ...drafts[provider.id], [credentialKey]: '' },
      }));
    }
  }

  private async updateProvider(
    provider: ProviderSettingsItem,
    credentials: Record<string, string>,
    successMessage: string,
  ): Promise<boolean> {
    this.busyProvider.set(provider.id);
    this.errorMessage.set('');
    this.statusMessage.set('');
    try {
      this.providers.set(await this.api.update(provider.id, credentials));
      this.statusMessage.set(successMessage);
      return true;
    } catch {
      this.errorMessage.set(`Credentials for ${provider.name} could not be saved.`);
      return false;
    } finally {
      this.busyProvider.set(null);
    }
  }
}
