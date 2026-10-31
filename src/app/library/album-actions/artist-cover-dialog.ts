import { Component, inject, input, OnInit, OnDestroy, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AlbumIdentifyApi,
  ArtistMatch,
  ArtistMatchDetail,
  IdentifyProvider,
} from '@shared/services/album-identify-api';
import { MusicLibraryApi } from '@shared/services/music-library-api';
import { ProviderSettingsApi } from '@shared/services/provider-settings-api';
import { errorMessage } from './error-message';

@Component({
  selector: 'app-artist-cover-dialog',
  imports: [FormsModule],
  templateUrl: './artist-cover-dialog.html',
  styleUrl: './album-dialog.scss',
})
export class ArtistCoverDialog implements OnInit, OnDestroy {
  private readonly api = inject(AlbumIdentifyApi);
  private readonly library = inject(MusicLibraryApi);
  private readonly providerSettingsApi = inject(ProviderSettingsApi);

  readonly artist = input.required<string>();
  readonly closed = output<boolean>();

  readonly providers = signal<IdentifyProvider[]>([]);
  readonly providerId = signal('');
  readonly candidates = signal<ArtistMatch[]>([]);
  readonly selectedCandidate = signal<ArtistMatch | null>(null);
  readonly selectedDetail = signal<ArtistMatchDetail | null>(null);
  readonly previewSrc = signal('');
  readonly busy = signal(false);
  readonly error = signal('');
  customUrl = '';
  private uploadedFile: File | null = null;
  private objectUrl: string | null = null;

  async ngOnInit(): Promise<void> {
    await this.run(async () => {
      const [providers, defaultProvider] = await Promise.all([
        this.api.listProviders(),
        this.providerSettingsApi.getDefaultAlbumProvider(),
      ]);
      const configured = providers.filter((provider) => provider.isConfigured);
      this.providers.set(configured);
      this.providerId.set(
        configured.find((provider) => provider.id === defaultProvider.providerId)?.id ??
          configured[0]?.id ??
          '',
      );
      if (!this.providerId()) {
        this.error.set('No metadata providers are currently available.');
        return;
      }
      await this.search();
    });
  }

  ngOnDestroy(): void {
    this.releaseObjectUrl();
  }

  close(changed = false): void {
    this.closed.emit(changed);
  }

  async changeProvider(providerId: string): Promise<void> {
    this.providerId.set(providerId);
    await this.search();
  }

  candidateImage(candidate: ArtistMatch): string | null {
    return candidate.imageUrl ? this.api.remoteImageUrl(candidate.imageUrl) : null;
  }

  async search(): Promise<void> {
    if (!this.providerId()) return;
    await this.run(async () => {
      this.candidates.set(await this.api.searchArtists(this.providerId(), this.artist()));
      this.selectedCandidate.set(null);
      this.selectedDetail.set(null);
      this.clearPreview();
    });
  }

  async selectCandidate(candidate: ArtistMatch): Promise<void> {
    await this.run(async () => {
      this.selectedCandidate.set(candidate);
      this.selectedDetail.set(null);
      this.clearPreview();
      const detail = await this.api.getArtistMatch(candidate.provider, candidate.id);
      const imageUrl =
        detail.imageDataUrl ??
        (detail.imageUrl ? this.api.remoteImageUrl(detail.imageUrl) : null);
      if (!imageUrl) throw new Error('This artist match has no picture.');
      this.selectedDetail.set(detail);
      this.previewSrc.set(imageUrl);
    });
  }

  chooseUpload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    if (!file.type.startsWith('image/')) {
      this.error.set('Choose an image file.');
      return;
    }
    this.error.set('');
    this.clearPreview();
    this.uploadedFile = file;
    this.objectUrl = URL.createObjectURL(file);
    this.previewSrc.set(this.objectUrl);
  }

  previewUrl(): void {
    const url = this.customUrl.trim();
    if (!/^https?:\/\//i.test(url)) {
      this.error.set('Enter a valid http or https image address.');
      return;
    }
    this.error.set('');
    this.clearPreview();
    this.previewSrc.set(this.api.remoteImageUrl(url));
  }

  async save(): Promise<void> {
    if (!this.previewSrc()) return;
    await this.run(async () => {
      const file = this.uploadedFile ?? (await this.api.fetchImage(this.previewSrc()));
      await this.library.updateEntityImage(this.artist(), null, file);
      this.close(true);
    });
  }

  private clearPreview(): void {
    this.releaseObjectUrl();
    this.uploadedFile = null;
    this.previewSrc.set('');
  }

  private releaseObjectUrl(): void {
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
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
