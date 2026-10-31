import { Component, computed, inject, input, OnDestroy, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AlbumIdentifyApi, CoverCandidate } from '@shared/services/album-identify-api';
import { MusicLibraryApi, MusicTrack } from '@shared/services/music-library-api';
import { AlbumMatchPicker } from './album-match-picker';
import { AlbumRef } from './album-actions';
import { errorMessage } from './error-message';

@Component({
  selector: 'app-album-cover-dialog',
  imports: [FormsModule, AlbumMatchPicker],
  templateUrl: './album-cover-dialog.html',
  styleUrl: './album-dialog.scss',
})
export class AlbumCoverDialog implements OnInit, OnDestroy {
  private readonly api = inject(AlbumIdentifyApi);
  private readonly library = inject(MusicLibraryApi);

  readonly album = input.required<AlbumRef>();
  readonly closed = output<boolean>();

  readonly tracks = signal<MusicTrack[]>([]);
  readonly covers = signal<CoverCandidate[]>([]);
  readonly warnings = signal<string[]>([]);
  readonly selectedCover = signal<{ id: string; previewUrl: string } | null>(null);
  readonly showSearch = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly trackIds = computed(() => this.tracks().map((track) => track.id));
  customUrl = '';
  private uploadedFile: File | null = null;
  private objectUrl: string | null = null;
  private changed = false;

  async ngOnInit(): Promise<void> {
    await this.run(async () => {
      this.tracks.set(await this.library.listAlbumTracks(this.album().artist, this.album().title));
      if (!this.tracks().length) throw new Error('This album has no tracks.');
      await this.loadCovers('musicbrainz', null);
    });
  }

  ngOnDestroy(): void {
    this.releaseObjectUrl();
  }

  close(): void {
    this.closed.emit(this.changed);
  }

  async searchedMatch(selection: { match: { id: string }; providerId: string }): Promise<void> {
    await this.run(() => this.loadCovers(selection.providerId, selection.match.id));
  }

  chooseCover(candidate: CoverCandidate): void {
    this.releaseObjectUrl();
    this.uploadedFile = null;
    this.selectedCover.set({ id: candidate.id, previewUrl: candidate.previewUrl });
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
    this.releaseObjectUrl();
    this.uploadedFile = file;
    this.objectUrl = URL.createObjectURL(file);
    this.selectedCover.set({ id: 'upload', previewUrl: this.objectUrl });
  }

  previewUrl(): void {
    const url = this.customUrl.trim();
    if (!/^https?:\/\//i.test(url)) {
      this.error.set('Enter a valid http or https image address.');
      return;
    }
    this.error.set('');
    this.releaseObjectUrl();
    this.uploadedFile = null;
    this.selectedCover.set({ id: `url:${url}`, previewUrl: this.api.remoteImageUrl(url) });
  }

  async save(): Promise<void> {
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
      await this.library.updateEntityImage(this.album().artist, this.album().title, file);
      this.changed = true;
      this.close();
    });
  }

  private async loadCovers(provider: string, matchId: string | null): Promise<void> {
    const result = await this.api.covers(this.trackIds(), provider, matchId);
    this.covers.set(result.candidates);
    this.warnings.set(result.warnings);
    this.releaseObjectUrl();
    this.uploadedFile = null;
    this.selectedCover.set(null);
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
