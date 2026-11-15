import { Component, effect, inject, OnInit, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AlbumTile } from '@shared/components/album-tile/album-tile';
import { ArtistTile } from '@shared/components/artist-tile/artist-tile';
import { AlbumActions } from '../library/album-actions/album-actions';
import { LibraryStore, LibraryView } from '@shared/services/library-store';
import {
  AlbumSummary,
  ArtistSummary,
  MusicLibraryApi,
  MusicTrack,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { LibraryOperationTracker } from '@shared/services/library-operation-tracker';

const HOME_COLLECTION_SIZE = 6;

@Component({
  selector: 'app-home-page',
  imports: [RouterLink, AlbumTile, ArtistTile],
  templateUrl: './home-page.html',
  styleUrl: './home-page.scss',
})
export class HomePage implements OnInit {
  private readonly library = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  private readonly operations = inject(LibraryOperationTracker);
  readonly albumActions = inject(AlbumActions);
  private lastChangeCount = this.albumActions.changeCount();

  readonly albums = signal<AlbumSummary[]>([]);
  readonly recentAlbums = signal<AlbumSummary[]>([]);
  readonly artists = signal<ArtistSummary[]>([]);
  readonly tracks = signal<MusicTrack[]>([]);
  readonly skeletonItems = Array.from({ length: HOME_COLLECTION_SIZE });
  readonly albumsLoading = signal(true);
  readonly recentAlbumsLoading = signal(true);
  readonly artistsLoading = signal(true);
  readonly tracksLoading = signal(true);
  readonly albumsError = signal('');
  readonly recentAlbumsError = signal('');
  readonly artistsError = signal('');
  readonly tracksError = signal('');

  constructor() {
    effect(() => {
      const changeCount = this.albumActions.changeCount();
      if (changeCount === this.lastChangeCount) return;
      this.lastChangeCount = changeCount;
      untracked(() => void this.loadHomeCollections());
    });
    effect(() => {
      const operation = this.operations.operation();
      if (operation?.state === 'completed') {
        untracked(() => void this.loadRecentAlbums());
      }
    });
  }

  ngOnInit(): void {
    void this.loadHomeCollections();
  }

  async loadHomeCollections(): Promise<void> {
    await Promise.all([this.loadAlbums(), this.loadRecentAlbums()]);
    await Promise.all([this.loadArtists(), this.loadTracks()]);
  }

  private async loadRecentAlbums(): Promise<void> {
    this.recentAlbumsLoading.set(true);
    this.recentAlbumsError.set('');
    try {
      this.recentAlbums.set(await this.library.listRecentAlbums());
    } catch {
      this.recentAlbumsError.set('Recently added albums could not be loaded. Check the music server connection.');
    } finally {
      this.recentAlbumsLoading.set(false);
    }
  }

  private async loadAlbums(): Promise<void> {
    this.albumsLoading.set(true);
    this.albumsError.set('');
    try {
      const albumPage = await this.library.listAlbumPage('', 1, HOME_COLLECTION_SIZE, true);
      this.albums.set(albumPage.items);
    } catch {
      this.albumsError.set('Albums could not be loaded. Check the music server connection.');
    } finally {
      this.albumsLoading.set(false);
    }
  }

  private async loadArtists(): Promise<void> {
    this.artistsLoading.set(true);
    this.artistsError.set('');
    try {
      const artistPage = await this.library.listArtistPage('', 1, HOME_COLLECTION_SIZE, true);
      this.artists.set(artistPage.items);
    } catch {
      this.artistsError.set('Artists could not be loaded. Check the music server connection.');
    } finally {
      this.artistsLoading.set(false);
    }
  }

  private async loadTracks(): Promise<void> {
    this.tracksLoading.set(true);
    this.tracksError.set('');
    try {
      const trackPage = await this.library.listTrackPage('', 1, HOME_COLLECTION_SIZE, true);
      this.tracks.set(trackPage.items);
    } catch {
      this.tracksError.set('Tracks could not be loaded. Check the music server connection.');
    } finally {
      this.tracksLoading.set(false);
    }
  }

  selectLibraryTab(view: LibraryView): void {
    this.store.collectionView.set(view);
  }

  playTrack(track: MusicTrack): void {
    this.player.play([track], 0);
  }
}
