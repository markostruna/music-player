import { Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AlbumIdentifyApi, AlbumMatch, AlbumMatchDetail } from '@shared/services/album-identify-api';
import { MusicLibraryApi, MusicTrack } from '@shared/services/music-library-api';
import { AlbumMatchPicker } from './album-match-picker';
import { AlbumRef } from './album-actions';
import { errorMessage } from './error-message';

interface TrackRow {
  id: number;
  fileName: string;
  title: string;
  artist: string;
  discNumber: number;
  trackNumber: number;
}

@Component({
  selector: 'app-album-tags-dialog',
  imports: [FormsModule, AlbumMatchPicker],
  templateUrl: './album-tags-dialog.html',
  styleUrl: './album-dialog.scss',
})
export class AlbumTagsDialog implements OnInit {
  private readonly api = inject(AlbumIdentifyApi);
  private readonly library = inject(MusicLibraryApi);

  readonly album = input.required<AlbumRef>();
  readonly closed = output<boolean>();

  readonly tracks = signal<MusicTrack[]>([]);
  readonly rows = signal<TrackRow[]>([]);
  readonly showLookup = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly trackIds = computed(() => this.tracks().map((track) => track.id));
  albumTitle = '';
  albumArtist = '';
  year: number | null = null;
  genre = '';
  private changed = false;

  async ngOnInit(): Promise<void> {
    await this.run(async () => {
      const tracks = await this.library.listAlbumTracks(this.album().artist, this.album().title);
      if (!tracks.length) throw new Error('This album has no tracks.');
      this.tracks.set(tracks);
      this.albumTitle = this.album().title;
      this.albumArtist = this.album().artist;
      this.year = tracks.find((track) => track.year > 0)?.year ?? null;
      this.genre = tracks.find((track) => track.genre)?.genre ?? '';
      this.rows.set(this.sortedTracks().map((track) => this.toRow(track)));
    });
  }

  close(): void {
    this.closed.emit(this.changed);
  }

  updateRow(index: number, field: keyof TrackRow, value: string | number): void {
    this.rows.update((rows) =>
      rows.map((row, position) => (position === index ? { ...row, [field]: value } : row)),
    );
  }

  async applyMatch(selection: { match: AlbumMatch; providerId: string }): Promise<void> {
    await this.run(async () => {
      const detail = await this.api.getMatch(selection.match.provider, selection.match.id);
      this.albumTitle = detail.match.title;
      this.albumArtist = detail.match.artist;
      this.year = detail.match.year;
      this.genre = detail.genre || this.genre;
      this.rows.set(this.pairTracks(detail));
      this.showLookup.set(false);
    });
  }

  async save(): Promise<void> {
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
        this.changed = true;
        failures.push(...results.filter((result) => !result.succeeded).map((r) => r.message));
      }
      if (failures.length) throw new Error(`Some tags could not be saved: ${failures[0]}`);
      this.close();
    });
  }

  private sortedTracks(): MusicTrack[] {
    return [...this.tracks()].sort(
      (a, b) =>
        (a.discNumber || 1) - (b.discNumber || 1) ||
        a.trackNumber - b.trackNumber ||
        a.title.localeCompare(b.title),
    );
  }

  private toRow(track: MusicTrack): TrackRow {
    return {
      id: track.id,
      fileName: track.title,
      title: track.title,
      artist: track.artist,
      discNumber: track.discNumber,
      trackNumber: track.trackNumber,
    };
  }

  private pairTracks(detail: AlbumMatchDetail): TrackRow[] {
    const local = this.sortedTracks();
    return local.map((track, index) => {
      const remote =
        detail.tracks.find(
          (item) =>
            item.trackNumber === track.trackNumber && item.discNumber === (track.discNumber || 1),
        ) ?? (local.length === detail.tracks.length ? detail.tracks[index] : undefined);
      return {
        ...this.toRow(track),
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
      this.error.set(errorMessage(error));
    } finally {
      this.busy.set(false);
    }
  }
}
