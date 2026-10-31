import { Component, inject, input, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AlbumIdentifyApi, AlbumMatch, IdentifyProvider } from '@shared/services/album-identify-api';
import { ProviderSettingsApi } from '@shared/services/provider-settings-api';
import { errorMessage } from './error-message';

type MatchMode = 'suggestions' | 'custom' | 'results';

@Component({
  selector: 'app-album-match-picker',
  imports: [FormsModule],
  templateUrl: './album-match-picker.html',
  styleUrl: './album-dialog.scss',
})
export class AlbumMatchPicker implements OnInit {
  private readonly api = inject(AlbumIdentifyApi);
  private readonly providerSettingsApi = inject(ProviderSettingsApi);

  readonly trackIds = input.required<number[]>();
  readonly artist = input.required<string>();
  readonly album = input.required<string>();
  readonly actionLabel = input('Select match');
  readonly disabled = input(false);
  readonly picked = output<{ match: AlbumMatch; providerId: string }>();

  readonly providers = signal<IdentifyProvider[]>([]);
  readonly providerId = signal('');
  readonly mode = signal<MatchMode>('suggestions');
  readonly matches = signal<AlbumMatch[]>([]);
  readonly selected = signal<AlbumMatch | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  customArtist = '';
  customAlbum = '';
  customYear: number | null = null;

  async ngOnInit(): Promise<void> {
    this.customArtist = this.artist();
    this.customAlbum = this.album();
    await this.run(async () => {
      const [providers, defaultProvider] = await Promise.all([
        this.api.listProviders(),
        this.providerSettingsApi.getDefaultAlbumProvider(),
      ]);
      this.providers.set(providers);
      const preferred =
        providers.find((item) => item.id === defaultProvider.providerId && item.isConfigured) ??
        providers.find((item) => item.isConfigured);
      this.providerId.set(preferred?.id ?? providers[0]?.id ?? '');
      if (preferred) await this.loadSuggestions();
    });
  }

  providerReady(): boolean {
    return !!this.providers().find((item) => item.id === this.providerId())?.isConfigured;
  }

  async changeProvider(id: string): Promise<void> {
    this.providerId.set(id);
    this.matches.set([]);
    this.selected.set(null);
    if (this.mode() === 'suggestions') await this.run(() => this.loadSuggestions());
    else this.mode.set('custom');
  }

  startCustom(): void {
    this.error.set('');
    this.selected.set(null);
    this.mode.set('custom');
  }

  backToSuggestions(): void {
    this.error.set('');
    this.selected.set(null);
    this.mode.set('suggestions');
    void this.run(() => this.loadSuggestions());
  }

  async search(): Promise<void> {
    if (!this.customArtist.trim() && !this.customAlbum.trim() && !this.customYear) {
      this.error.set('Enter an artist, album title or release year.');
      return;
    }
    await this.run(async () => {
      this.matches.set(
        await this.api.search(this.providerId(), {
          artist: this.customArtist.trim(),
          album: this.customAlbum.trim(),
          year: this.customYear,
        }),
      );
      this.selected.set(null);
      this.mode.set('results');
    });
  }

  pick(): void {
    const match = this.selected();
    if (match) this.picked.emit({ match, providerId: this.providerId() });
  }

  private async loadSuggestions(): Promise<void> {
    if (!this.providerReady()) return;
    const result = await this.api.suggest(this.trackIds(), this.providerId());
    this.matches.set(result.matches);
    this.customArtist = result.tagArtist ?? this.artist();
    this.customAlbum = result.tagAlbum ?? this.album();
    this.customYear = result.tagYear;
    this.selected.set(null);
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await action();
    } catch (error) {
      this.error.set(errorMessage(error));
    } finally {
      this.busy.set(false);
    }
  }
}
