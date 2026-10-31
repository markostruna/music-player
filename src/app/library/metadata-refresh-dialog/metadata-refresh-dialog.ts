import { Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AlbumIdentifyApi,
  AlbumMatch,
  AlbumMatchDetail,
  ArtistMatch,
  ArtistMatchDetail,
  IdentifyProvider,
} from '@shared/services/album-identify-api';
import {
  MetadataRefreshPreview,
  MetadataRefreshResult,
  MusicLibraryApi,
} from '@shared/services/music-library-api';
import { ProviderSettingsApi } from '@shared/services/provider-settings-api';

@Component({
  selector: 'app-metadata-refresh-dialog',
  imports: [FormsModule],
  templateUrl: './metadata-refresh-dialog.html',
  styleUrl: './metadata-refresh-dialog.scss',
})
export class MetadataRefreshDialog implements OnInit {
  private readonly identifyApi = inject(AlbumIdentifyApi);
  private readonly libraryApi = inject(MusicLibraryApi);
  private readonly providerSettingsApi = inject(ProviderSettingsApi);

  readonly artist = input.required<string>();
  readonly album = input<string | null>(null);
  readonly saved = output<{ result: MetadataRefreshResult; providerId: string }>();
  readonly closed = output<void>();

  readonly providers = signal<IdentifyProvider[]>([]);
  readonly providerId = signal('');
  readonly artistCandidates = signal<ArtistMatch[]>([]);
  readonly albumCandidates = signal<AlbumMatch[]>([]);
  readonly selectedArtistCandidate = signal<ArtistMatch | null>(null);
  readonly selectedAlbumCandidate = signal<AlbumMatch | null>(null);
  readonly artistPreview = signal<ArtistMatchDetail | null>(null);
  readonly albumPreview = signal<AlbumMatchDetail | null>(null);
  readonly albumMetadataPreview = signal<MetadataRefreshPreview | null>(null);
  readonly isSearching = signal(false);
  readonly isLoadingPreview = signal(false);
  readonly isSaving = signal(false);
  readonly errorMessage = signal('');
  readonly isAlbum = computed(() => this.album() !== null);
  readonly canSave = computed(() =>
    this.isAlbum()
      ? this.albumMetadataPreview()?.found === true
      : this.artistPreview() !== null,
  );
  readonly previewImageUrl = computed(() => {
    if (this.isAlbum()) {
      const imageDataUrl = this.albumMetadataPreview()?.imageDataUrl;
      if (imageDataUrl) return imageDataUrl;
      const url = this.albumPreview()?.match.coverUrl;
      return url ? this.identifyApi.remoteImageUrl(url) : null;
    }

    const preview = this.artistPreview();
    return preview?.imageDataUrl
      ?? (preview?.imageUrl ? this.identifyApi.remoteImageUrl(preview.imageUrl) : null);
  });

  ngOnInit(): void {
    void this.load();
  }

  close(): void {
    if (!this.isSaving()) this.closed.emit();
  }

  async changeProvider(providerId: string): Promise<void> {
    this.providerId.set(providerId);
    await this.search();
  }

  async selectArtistCandidate(candidate: ArtistMatch): Promise<void> {
    this.selectedArtistCandidate.set(candidate);
    this.selectedAlbumCandidate.set(null);
    this.artistPreview.set(null);
    this.albumPreview.set(null);
    this.albumMetadataPreview.set(null);
    this.isLoadingPreview.set(true);
    this.errorMessage.set('');
    try {
      this.artistPreview.set(await this.identifyApi.getArtistMatch(candidate.provider, candidate.id));
    } catch {
      this.errorMessage.set('The selected artist candidate could not be previewed.');
    } finally {
      this.isLoadingPreview.set(false);
    }
  }

  async selectAlbumCandidate(candidate: AlbumMatch): Promise<void> {
    this.selectedAlbumCandidate.set(candidate);
    this.selectedArtistCandidate.set(null);
    this.artistPreview.set(null);
    this.albumPreview.set(null);
    this.albumMetadataPreview.set(null);
    this.isLoadingPreview.set(true);
    this.errorMessage.set('');
    try {
      const [detail, metadata] = await Promise.all([
        this.identifyApi.getMatch(candidate.provider, candidate.id),
        this.libraryApi.previewAlbumMetadata(
          this.artist(),
          this.album()!,
          candidate.provider,
          candidate.id,
        ),
      ]);
      this.albumPreview.set(detail);
      this.albumMetadataPreview.set(metadata);
      if (!metadata.found) {
        this.errorMessage.set('The selected candidate has no metadata to preview.');
      }
    } catch {
      this.errorMessage.set('The selected album candidate could not be previewed.');
    } finally {
      this.isLoadingPreview.set(false);
    }
  }

  async save(): Promise<void> {
    const candidateId = this.selectedAlbumCandidate()?.id ?? this.selectedArtistCandidate()?.id;
    if (!candidateId || !this.providerId() || this.isSaving()) return;

    this.isSaving.set(true);
    this.errorMessage.set('');
    try {
      const result = await this.libraryApi.refreshInformation(
        this.artist(),
        this.album(),
        this.providerId(),
        candidateId,
      );
      if (!result.found) {
        this.errorMessage.set('The selected provider could not retrieve this candidate.');
        return;
      }

      this.saved.emit({ result, providerId: this.providerId() });
      this.closed.emit();
    } catch {
      this.errorMessage.set('The selected metadata could not be saved. Please try again.');
    } finally {
      this.isSaving.set(false);
    }
  }

  private async load(): Promise<void> {
    this.isSearching.set(true);
    this.errorMessage.set('');
    try {
      const [providers, defaultProvider] = await Promise.all([
        this.identifyApi.listProviders(),
        this.providerSettingsApi.getDefaultAlbumProvider(),
      ]);
      const configured = providers.filter((provider) => provider.isConfigured);
      this.providers.set(configured);
      this.providerId.set(
        configured.find((provider) => provider.id === defaultProvider.providerId)?.id
          ?? configured[0]?.id
          ?? '',
      );
      if (!this.providerId()) {
        this.errorMessage.set('No metadata providers are currently available.');
        return;
      }

      await this.search();
    } catch {
      this.errorMessage.set('Metadata providers could not be loaded.');
    } finally {
      this.isSearching.set(false);
    }
  }

  private async search(): Promise<void> {
    if (!this.providerId()) return;
    this.isSearching.set(true);
    this.errorMessage.set('');
    this.artistCandidates.set([]);
    this.albumCandidates.set([]);
    this.selectedArtistCandidate.set(null);
    this.selectedAlbumCandidate.set(null);
    this.artistPreview.set(null);
    this.albumPreview.set(null);
    this.albumMetadataPreview.set(null);
    try {
      if (this.isAlbum()) {
        this.albumCandidates.set(
          await this.identifyApi.search(this.providerId(), {
            artist: this.artist(),
            album: this.album()!,
          }),
        );
      } else {
        this.artistCandidates.set(
          await this.identifyApi.searchArtists(this.providerId(), this.artist()),
        );
      }
    } catch {
      this.errorMessage.set('Candidates could not be loaded from this provider.');
    } finally {
      this.isSearching.set(false);
    }
  }
}
