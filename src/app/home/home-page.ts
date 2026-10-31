import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LibraryStore, LibraryView } from '@shared/services/library-store';
import {
  AlbumSummary,
  ArtistSummary,
  MusicLibraryApi,
  MusicTrack,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';

const HOME_COLLECTION_SIZE = 6;

@Component({
  selector: 'app-home-page',
  imports: [RouterLink],
  templateUrl: './home-page.html',
  styleUrl: './home-page.scss',
})
export class HomePage implements OnInit {
  private readonly library = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);

  readonly albums = signal<AlbumSummary[]>([]);
  readonly artists = signal<ArtistSummary[]>([]);
  readonly tracks = signal<MusicTrack[]>([]);
  readonly isLoading = signal(true);
  readonly errorMessage = signal('');

  ngOnInit(): void {
    void this.loadHomeCollections();
  }

  async loadHomeCollections(): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      const [albumPage, artistPage, trackPage] = await Promise.all([
        this.library.listAlbumPage('', 1, HOME_COLLECTION_SIZE, true),
        this.library.listArtistPage('', 1, HOME_COLLECTION_SIZE, true),
        this.library.listTrackPage('', 1, HOME_COLLECTION_SIZE, true),
      ]);
      this.albums.set(albumPage.items);
      this.artists.set(artistPage.items);
      this.tracks.set(trackPage.items);
    } catch {
      this.errorMessage.set('Your collection could not be loaded. Check the music server connection.');
    } finally {
      this.isLoading.set(false);
    }
  }

  selectLibraryTab(view: LibraryView): void {
    this.store.collectionView.set(view);
  }

  playTrack(track: MusicTrack): void {
    const queue = this.tracks();
    this.player.play(queue, queue.findIndex((item) => item.id === track.id));
  }
}
