import { Component, computed, inject, input, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AlbumIdentifyApi, AlbumMovePlan } from '@shared/services/album-identify-api';
import { MusicLibraryApi, MusicTrack, SourceRoot } from '@shared/services/music-library-api';
import { AlbumRef } from './album-actions';
import { errorMessage } from './error-message';

@Component({
  selector: 'app-album-move-dialog',
  imports: [FormsModule],
  templateUrl: './album-move-dialog.html',
  styleUrl: './album-dialog.scss',
})
export class AlbumMoveDialog implements OnInit {
  private readonly api = inject(AlbumIdentifyApi);
  private readonly library = inject(MusicLibraryApi);

  readonly album = input.required<AlbumRef>();
  readonly closed = output<boolean>();

  readonly tracks = signal<MusicTrack[]>([]);
  readonly roots = signal<SourceRoot[]>([]);
  readonly plan = signal<AlbumMovePlan | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly result = signal('');
  readonly trackIds = computed(() => this.tracks().map((track) => track.id));
  destinationRootId = 0;
  private changed = false;

  async ngOnInit(): Promise<void> {
    await this.run(async () => {
      const [tracks, roots] = await Promise.all([
        this.library.listAlbumTracks(this.album().artist, this.album().title),
        this.library.listRoots(),
      ]);
      if (!tracks.length) throw new Error('This album has no tracks.');
      this.tracks.set(tracks);
      this.roots.set(roots.filter((root) => root.isEnabled));
      this.destinationRootId = tracks[0].sourceRootId || this.roots()[0]?.id || 0;
      await this.refreshPlan();
    });
  }

  close(): void {
    this.closed.emit(this.changed);
  }

  async changeDestination(id: number): Promise<void> {
    this.destinationRootId = Number(id);
    await this.run(() => this.refreshPlan());
  }

  async move(): Promise<void> {
    await this.run(async () => {
      const moved = await this.api.move(this.request());
      this.changed = true;
      this.plan.set(null);
      this.result.set(`Moved ${moved.movedTracks} tracks to ${moved.newPath}.`);
    });
  }

  private async refreshPlan(): Promise<void> {
    this.plan.set(await this.api.previewMove(this.request()));
  }

  private request() {
    const year = this.tracks().find((track) => track.year > 0)?.year;
    return {
      trackIds: this.trackIds(),
      destinationRootId: this.destinationRootId,
      artist: this.album().artist,
      album: this.album().title,
      year: year || null,
    };
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
