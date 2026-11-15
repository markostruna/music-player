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
  readonly searchQuery = signal<{ artist: string; album: string; year: number | null }>({
    artist: '',
    album: '',
    year: null,
  });
  readonly isCustomSearch = signal(false);
  readonly selectedArtistCandidate = signal<ArtistMatch | null>(null);
  readonly selectedAlbumCandidate = signal<AlbumMatch | null>(null);
  readonly artistPreview = signal<ArtistMatchDetail | null>(null);
  readonly albumPreview = signal<AlbumMatchDetail | null>(null);
  readonly albumMetadataPreview = signal<MetadataRefreshPreview | null>(null);
  readonly isSearching = signal(false);
  readonly isLoadingPreview = signal(false);
  readonly isSaving = signal(false);
  readonly errorMessage = signal('');
  customArtist = '';
  customAlbum = '';
  customYear: number | null = null;
  readonly rankedAlbumCandidates = computed(() =>
    [...this.albumCandidates()].sort(
      (left, right) => this.matchScore(right) - this.matchScore(left),
    ),
  );
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
    if (!this.isCustomSearch()) await this.searchMatches();
  }

  openCustomSearch(): void {
    const query = this.searchQuery();
    this.customArtist = query.artist;
    this.customAlbum = query.album;
    this.customYear = query.year;
    this.errorMessage.set('');
    this.isCustomSearch.set(true);
  }

  async searchCustom(): Promise<void> {
    const query = {
      artist: this.customArtist.trim(),
      album: this.customAlbum.trim(),
      year: this.customYear,
    };
    if (!query.artist && !query.album && !query.year) {
      this.errorMessage.set('Enter an artist, album title or release year.');
      return;
    }

    this.searchQuery.set(query);
    this.isCustomSearch.set(false);
    await this.searchMatches();
  }

  candidateImageUrl(candidate: AlbumMatch): string | null {
    return candidate.coverUrl ? this.identifyApi.remoteImageUrl(candidate.coverUrl) : null;
  }

  matchScore(candidate: AlbumMatch): number {
    const query = this.searchQuery();
    let weightedScore = 0;
    let totalWeight = 0;
    if (query.album) {
      weightedScore += this.textSimilarity(candidate.title, query.album) * 60;
      totalWeight += 60;
    }
    if (query.artist) {
      weightedScore += this.textSimilarity(candidate.artist, query.artist) * 30;
      totalWeight += 30;
    }
    if (query.year && candidate.year) {
      weightedScore += (candidate.year === query.year ? 1 : 0) * 10;
      totalWeight += 10;
    }
    return totalWeight ? Math.round((weightedScore / totalWeight) * 100) : 0;
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

      this.searchQuery.set({
        artist: this.artist(),
        album: this.album() ?? '',
        year: null,
      });
      this.customArtist = this.artist();
      this.customAlbum = this.album() ?? '';
      await this.searchMatches();
    } catch {
      this.errorMessage.set('Metadata providers could not be loaded.');
    } finally {
      this.isSearching.set(false);
    }
  }

  private async searchMatches(): Promise<void> {
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
          await this.identifyApi.search(this.providerId(), this.searchQuery()),
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

  private textSimilarity(left: string, right: string): number {
    const tokens = (value: string) =>
      value
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, ' ')
        .trim()
        .split(/\s+/)
        .filter(Boolean);
    const leftTokens = tokens(left);
    const rightTokens = tokens(right);
    if (!leftTokens.length || !rightTokens.length) return 0;
    const rightSet = new Set(rightTokens);
    const shared = leftTokens.filter((token) => rightSet.has(token)).length;
    return (2 * shared) / (leftTokens.length + rightTokens.length);
  }
}
