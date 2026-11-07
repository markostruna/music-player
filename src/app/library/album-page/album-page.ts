import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { formatTime } from '@shared/utils/format-time';
import { MusicLibraryApi, MusicTrack } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { AlbumActions, AlbumRef } from '../album-actions/album-actions';
import { distinctUntilChanged, map } from 'rxjs';

@Component({
  selector: 'app-album-page',
  imports: [RouterLink],
  templateUrl: './album-page.html',
})
export class AlbumPage {
  readonly albumActions = inject(AlbumActions);
  private readonly libraryApi = inject(MusicLibraryApi);
  private readonly player = inject(PlayerService);
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.paramMap);
  private readonly albumTracks = signal<MusicTrack[]>([]);
  private readonly relatedTracks = signal<MusicTrack[]>([]);
  private readonly loadingError = signal('');
  readonly relatedAlbumsError = signal('');
  private albumRequestId = 0;

  private lastChangeCount = this.albumActions.changeCount();
  readonly isLoading = signal(true);
  readonly errorMessage = computed(() => {
    if (this.loadingError()) return this.loadingError();
    return !this.isLoading() && !this.album()
      ? 'This album could not be found in your library.'
      : '';
  });
  readonly currentTrack = this.player.currentTrack;
  readonly isPlaying = this.player.isPlaying;
  readonly formatTime = formatTime;
  formatAlbumDuration(seconds: number): string {
    const totalMinutes = Number.isFinite(seconds) && seconds > 0 ? Math.floor(seconds / 60) : 0;
    return `${Math.floor(totalMinutes / 60)}h ${totalMinutes % 60}min`;
  }
  readonly album = computed(() => {
    const albumTracks = [...this.albumTracks()].sort(
      (first, second) =>
        first.discNumber - second.discNumber ||
        first.trackNumber - second.trackNumber ||
        first.title.localeCompare(second.title),
    );
    const selectedTrack = albumTracks[0];
    if (!selectedTrack) return null;

    const artist = selectedTrack.albumArtist.trim() || selectedTrack.artist;
    return {
      title: selectedTrack.album,
      artist,
      coverUrl: selectedTrack.albumCoverUrl,
      releaseYear: albumTracks.find((track) => track.year > 0)?.year,
      tracks: albumTracks,
      totalDurationSeconds: albumTracks.reduce((total, track) => total + track.durationSeconds, 0),
    };
  });
  readonly relatedAlbums = computed(() => {
    const currentAlbum = this.album();
    if (!currentAlbum) return [];

    const albums = new Map<
      string,
      { key: string; title: string; coverUrl: string; year: number; tracks: MusicTrack[] }
    >();
    for (const track of this.relatedTracks()) {
      const artist = track.albumArtist.trim() || track.artist.trim();
      if (
        artist.toLowerCase() !== currentAlbum.artist.toLowerCase() ||
        track.album.trim().toLowerCase() === currentAlbum.title.toLowerCase()
      ) {
        continue;
      }
      const key = `${artist.toLowerCase()}\0${track.album.toLowerCase()}`;
      const existing = albums.get(key);
      if (existing) {
        existing.tracks.push(track);
        existing.year ||= track.year;
      } else {
        albums.set(key, {
          key,
          title: track.album,
          coverUrl: track.tileCoverUrl,
          year: track.year,
          tracks: [track],
        });
      }
    }

    return [...albums.values()].sort(
      (first, second) => second.year - first.year || first.title.localeCompare(second.title),
    );
  });

  constructor() {
    effect(() => {
      const changeCount = this.albumActions.changeCount();
      if (changeCount === this.lastChangeCount) return;
      this.lastChangeCount = changeCount;
      const trackId = untracked(() => Number(this.params()?.get('trackId')));
      if (Number.isInteger(trackId) && trackId > 0) {
        untracked(() => void this.loadAlbum(trackId));
      }
    });
    this.route.paramMap
      .pipe(
        map((params) => Number(params.get('trackId'))),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((trackId) => void this.loadAlbum(trackId));
  }

  private async loadAlbum(trackId: number): Promise<void> {
    const requestId = ++this.albumRequestId;
    this.isLoading.set(true);
    this.loadingError.set('');
    this.albumTracks.set([]);
    this.relatedTracks.set([]);
    this.relatedAlbumsError.set('');
    if (!Number.isInteger(trackId) || trackId < 1) {
      this.isLoading.set(false);
      return;
    }

    try {
      const tracks = await this.libraryApi.listAlbumTracksByTrackId(trackId);
      if (requestId !== this.albumRequestId) return;
      this.albumTracks.set(tracks);
      const selectedTrack = tracks[0];
      if (selectedTrack) {
        const artist = selectedTrack.albumArtist.trim() || selectedTrack.artist;
        try {
          const relatedTracks = await this.libraryApi.listArtistTracks(artist);
          if (requestId === this.albumRequestId) this.relatedTracks.set(relatedTracks);
        } catch {
          if (requestId === this.albumRequestId) {
            this.relatedAlbumsError.set('More albums by this artist could not be loaded.');
          }
        }
      }
    } catch {
      if (requestId === this.albumRequestId) {
        this.loadingError.set('The album could not be loaded. Check the music server connection.');
      }
    } finally {
      if (requestId === this.albumRequestId) this.isLoading.set(false);
    }
  }

  playAlbum(tracks: MusicTrack[]): void {
    this.player.play(tracks, 0);
  }

  playTrack(track: MusicTrack): void {
    this.player.play([track], 0);
  }

  albumRef(album: { artist: string; title: string }): AlbumRef {
    return { artist: album.artist, title: album.title };
  }
}
