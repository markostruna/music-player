import { Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AlbumActions, AlbumRef } from '../../../library/album-actions/album-actions';

@Component({
  selector: 'app-album-tile',
  imports: [RouterLink],
  templateUrl: './album-tile.html',
})
export class AlbumTile {
  readonly title = input.required<string>();
  readonly artist = input.required<string>();
  readonly coverUrl = input.required<string>();
  readonly trackId = input.required<number>();
  readonly year = input<number | null>(null);

  readonly albumActions = inject(AlbumActions);
  readonly albumRef = computed<AlbumRef>(() => ({
    artist: this.artist(),
    title: this.title(),
  }));
}
