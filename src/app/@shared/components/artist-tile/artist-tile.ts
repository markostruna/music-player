import { Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthState } from '@shared/services/auth-state';
import { AlbumActions } from '../../../library/album-actions/album-actions';

@Component({
  selector: 'app-artist-tile',
  imports: [RouterLink],
  templateUrl: './artist-tile.html',
})
export class ArtistTile {
  readonly name = input.required<string>();
  readonly imageUrl = input<string | null>(null);
  readonly coverUrl = input.required<string>();
  readonly imageMissing = input(false);
  readonly albumCount = input.required<number>();

  readonly albumActions = inject(AlbumActions);
  readonly isAdmin = inject(AuthState).isAdmin;
  readonly artistImageUrl = computed(() => this.imageUrl() || this.coverUrl());
}
