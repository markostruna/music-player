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
  readonly fileExtensions = input<readonly string[]>([]);
  readonly trackCount = input<number | null>(null);

  readonly albumActions = inject(AlbumActions);
  readonly albumRef = computed<AlbumRef>(() => ({
    artist: this.artist(),
    title: this.title(),
  }));
  readonly formatBadge = computed(() => {
    const formats = [
      ...new Set(
        this.fileExtensions().map((extension) => {
          const normalized = extension.replace(/^\./, '').toLowerCase();
          return normalized === 'wav' || normalized === 'wave' ? 'WAVE' : normalized.toUpperCase();
        }),
      ),
    ].sort();
    if (!formats.length) return null;

    const isFlac = formats.length === 1 && formats[0] === 'FLAC';
    const isSingleFlacFile = isFlac && this.trackCount() === 1;
    return {
      label: isSingleFlacFile ? 'FLAC · 1 FILE' : formats.join(' + '),
      className: isSingleFlacFile
        ? 'tile-format-badge--flac-single'
        : isFlac
          ? 'tile-format-badge--flac-album'
          : formats.length > 1
            ? 'tile-format-badge--mixed'
            : 'tile-format-badge--other',
      title: `Audio format${formats.length > 1 ? 's' : ''}: ${formats.join(', ')}`,
    };
  });
}
