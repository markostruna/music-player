import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AlbumIdentifyApi,
  AlbumMatch,
  AlbumMatchDetail,
  AlbumMovePlan,
  CoverCandidate,
  IdentifyProvider,
} from '@shared/services/album-identify-api';
import { MusicLibraryApi, MusicTrack, SourceRoot } from '@shared/services/music-library-api';

type Step = 'match' | 'review' | 'cover' | 'move' | 'done';
type MatchMode = 'suggestions' | 'custom' | 'results';

interface TrackRow {
  id: number;
  fileName: string;
  title: string;
  artist: string;
  discNumber: number;
  trackNumber: number;
}

export interface IdentifyAlbumRequest {
  title: string;
  artist: string;
  tracks: MusicTrack[];
}

const STEPS: { id: Step; label: string }[] = [
  { id: 'match', label: 'Match' },
  { id: 'review', label: 'Tags' },
  { id: 'cover', label: 'Cover' },
  { id: 'move', label: 'Move' },
];

@Component({
  selector: 'app-identify-album-dialog',
  imports: [FormsModule],
  templateUrl: './identify-album-dialog.html',
  styleUrl: './identify-album-dialog.scss',
})
export class IdentifyAlbumDialog implements OnInit {
  private readonly api = inject(AlbumIdentifyApi);
  private readonly library = inject(MusicLibraryApi);

  readonly album = input.required<IdentifyAlbumRequest>();
  readonly closed = output<boolean>();

  readonly steps = STEPS;
  readonly step = signal<Step>('match');
  readonly busy = signal(false);
  readonly error = signal('');
  readonly changed = signal(false);

  readonly providers = signal<IdentifyProvider[]>([]);
  readonly providerId = signal('');
  readonly matchMode = signal<MatchMode>('suggestions');
  readonly matches = signal<AlbumMatch[]>([]);
  readonly selectedMatch = signal<AlbumMatch | null>(null);
  customArtist = '';
  customAlbum = '';
  customYear: number | null = null;

  detail: AlbumMatchDetail | null = null;
  albumTitle = '';
  albumArtist = '';
  year: number | null = null;
  genre = '';
  readonly rows = signal<TrackRow[]>([]);

  readonly covers = signal<CoverCandidate[]>([]);
  readonly coverWarnings = signal<string[]>([]);
  readonly selectedCover = signal<{ id: string; previewUrl: string } | null>(null);
  customUrl = '';
  private uploadedFile: File | null = null;

  readonly roots = signal<SourceRoot[]>([]);
  destinationRootId = 0;
  readonly movePlan = signal<AlbumMovePlan | null>(null);
  readonly moveResult = signal('');

  readonly stepIndex = computed(() => STEPS.findIndex((step) => step.id === this.step()));
  readonly providerReady = computed(
    () => this.providers().find((provider) => provider.id === this.providerId())?.isConfigured,
  );
  private readonly trackIds = computed(() => this.album().tracks.map((track) => track.id));

  async ngOnInit(): Promise<void> {
    await this.run(async () => {
      const providers = await this.api.listProviders();
      this.providers.set(providers);
      const saved = localStorage.getItem('identify-provider');
      const preferred =
        providers.find((provider) => provider.id === saved && provider.isConfigured) ??
        providers.find((provider) => provider.isConfigured);
      this.providerId.set(preferred?.id ?? providers[0]?.id ?? '');
      this.customArtist = this.album().artist;
      this.customAlbum = this.album().title;
      if (preferred) await this.loadSuggestions();
    });
  }

  close(): void {
    this.closed.emit(this.changed());
  }

  async changeProvider(id: string): Promise<void> {
    this.providerId.set(id);
    localStorage.setItem('identify-provider', id);
    this.matches.set([]);
    this.selectedMatch.set(null);
    if (this.matchMode() === 'suggestions') await this.run(() => this.loadSuggestions());
    else if (this.matchMode() === 'results') this.matchMode.set('custom');
  }

  startCustom(): void {
    this.error.set('');
    this.selectedMatch.set(null);
    this.matchMode.set('custom');
  }

  backToSuggestions(): void {
    this.error.set('');
    this.selectedMatch.set(null);
    this.matchMode.set('suggestions');
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
      this.selectedMatch.set(null);
      this.matchMode.set('results');
    });
  }

  async acceptMatch(): Promise<void> {
    const match = this.selectedMatch();
    if (!match) return;
    await this.run(async () => {
      const detail = await this.api.getMatch(match.provider, match.id);
      this.detail = detail;
      this.albumTitle = detail.match.title;
      this.albumArtist = detail.match.artist;
      this.year = detail.match.year;
      this.genre = detail.genre || (this.album().tracks.find((track) => track.genre)?.genre ?? '');
      this.rows.set(this.pairTracks(detail));
      this.step.set('review');
    });
  }

  updateRow(index: number, field: keyof TrackRow, value: string | number): void {
    this.rows.update((rows) =>
      rows.map((row, position) => (position === index ? { ...row, [field]: value } : row)),
    );
  }

  async acceptTags(): Promise<void> {
    if (!this.albumTitle.trim() || !this.albumArtist.trim()) {
      this.error.set('Album title and album artist are required.');
      return;
    }
    await this.run(async () => {
      const failures: string[] = [];
      for (const row of this.rows()) {
        const results = await this.library.updateMetadata([row.id], {
          title: row.title,
          artist: row.artist || this.albumArtist,
          album: this.albumTitle.trim(),
          albumArtist: this.albumArtist.trim(),
          genre: this.genre,
          trackNumber: Number(row.trackNumber) || 0,
          discNumber: Number(row.discNumber) || 0,
          year: this.year ?? 0,
        });
        this.changed.set(true);
        failures.push(...results.filter((result) => !result.succeeded).map((r) => r.message));
      }
      if (failures.length) {
        throw new Error(`Some tags could not be saved: ${failures[0]}`);
      }
      await this.loadCovers();
      this.step.set('cover');
    });
  }

  chooseCover(candidate: CoverCandidate): void {
    this.selectedCover.set({ id: candidate.id, previewUrl: candidate.previewUrl });
  }

  chooseUpload(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    if (!file.type.startsWith('image/')) {
      this.error.set('Choose an image file.');
      return;
    }
    this.error.set('');
    this.uploadedFile = file;
    this.selectedCover.set({ id: 'upload', previewUrl: URL.createObjectURL(file) });
  }

  previewUrl(): void {
    const url = this.customUrl.trim();
    if (!/^https?:\/\//i.test(url)) {
      this.error.set('Enter a valid http or https image address.');
      return;
    }
    this.error.set('');
    this.selectedCover.set({ id: `url:${url}`, previewUrl: this.api.remoteImageUrl(url) });
  }

  async acceptCover(): Promise<void> {
    const cover = this.selectedCover();
    if (!cover) return;
    await this.run(async () => {
      const file =
        cover.id === 'upload' && this.uploadedFile
          ? this.uploadedFile
          : await this.api.fetchImage(cover.previewUrl);
      const results = await this.library.updateCover(this.trackIds(), file);
      const failed = results.find((result) => !result.succeeded);
      if (failed) throw new Error(`The cover could not be saved: ${failed.message}`);
      await this.library.updateEntityImage(this.albumArtist.trim(), this.albumTitle.trim(), file);
      this.changed.set(true);
      await this.enterMove();
    });
  }

  async skipCover(): Promise<void> {
    await this.run(() => this.enterMove());
  }

  async changeDestination(id: number): Promise<void> {
    this.destinationRootId = Number(id);
    await this.run(() => this.refreshPlan());
  }

  async moveAlbum(): Promise<void> {
    await this.run(async () => {
      const result = await this.api.move(this.moveRequest());
      this.changed.set(true);
      this.moveResult.set(`Moved ${result.movedTracks} tracks to ${result.newPath}.`);
      this.step.set('done');
    });
  }

  finishWithoutMove(): void {
    this.moveResult.set('');
    this.step.set('done');
  }

  private async loadSuggestions(): Promise<void> {
    if (!this.providerReady()) return;
    const result = await this.api.suggest(this.trackIds(), this.providerId());
    this.matches.set(result.matches);
    this.customArtist = result.tagArtist ?? this.album().artist;
    this.customAlbum = result.tagAlbum ?? this.album().title;
    this.customYear = result.tagYear;
    this.selectedMatch.set(null);
  }

  private async loadCovers(): Promise<void> {
    const result = await this.api.covers(
      this.trackIds(),
      this.providerId(),
      this.detail?.match.id ?? null,
    );
    this.covers.set(result.candidates);
    this.coverWarnings.set(result.warnings);
    this.selectedCover.set(null);
    this.uploadedFile = null;
  }

  private async enterMove(): Promise<void> {
    if (!this.roots().length) {
      this.roots.set((await this.library.listRoots()).filter((root) => root.isEnabled));
    }
    const current = this.album().tracks[0]?.sourceRootId;
    this.destinationRootId = this.destinationRootId || current || this.roots()[0]?.id || 0;
    await this.refreshPlan();
    this.step.set('move');
  }

  private async refreshPlan(): Promise<void> {
    this.movePlan.set(await this.api.previewMove(this.moveRequest()));
  }

  private moveRequest() {
    return {
      trackIds: this.trackIds(),
      destinationRootId: this.destinationRootId,
      artist: this.albumArtist.trim(),
      album: this.albumTitle.trim(),
      year: this.year || null,
    };
  }

  private pairTracks(detail: AlbumMatchDetail): TrackRow[] {
    const local = [...this.album().tracks].sort(
      (a, b) =>
        (a.discNumber || 1) - (b.discNumber || 1) ||
        a.trackNumber - b.trackNumber ||
        a.title.localeCompare(b.title),
    );
    return local.map((track, index) => {
      const remote =
        detail.tracks.find(
          (item) =>
            item.trackNumber === track.trackNumber && item.discNumber === (track.discNumber || 1),
        ) ?? (local.length === detail.tracks.length ? detail.tracks[index] : undefined);
      return {
        id: track.id,
        fileName: track.title,
        title: remote?.title ?? track.title,
        artist: remote?.artist ?? track.artist,
        discNumber: remote?.discNumber ?? track.discNumber,
        trackNumber: remote?.trackNumber ?? track.trackNumber,
      };
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await action();
    } catch (error) {
      this.error.set(this.messageFor(error));
    } finally {
      this.busy.set(false);
    }
  }

  private messageFor(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      const body = error.error;
      if (typeof body === 'string' && body) return body;
      if (body && typeof body === 'object' && 'detail' in body) return String(body.detail);
      if (error.status === 403) return 'You do not have permission to do that.';
    }
    return error instanceof Error ? error.message : 'Something went wrong. Please try again.';
  }
}
