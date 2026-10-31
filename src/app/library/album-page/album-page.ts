import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthState } from '@shared/services/auth-state';
import { formatTime } from '@shared/utils/format-time';
import {
  MetadataRefreshResult,
  MusicLibraryApi,
  MusicTrack,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { ToastService } from '@shared/services/toast-service';
import { AlbumActions } from '../album-actions/album-actions';
import { MetadataRefreshDialog } from '../metadata-refresh-dialog/metadata-refresh-dialog';
import { distinctUntilChanged, map } from 'rxjs';

@Component({
  selector: 'app-album-page',
  imports: [MetadataRefreshDialog, RouterLink],
  templateUrl: './album-page.html',
})
export class AlbumPage {
  private readonly auth = inject(AuthState);
  readonly albumActions = inject(AlbumActions);
  private readonly libraryApi = inject(MusicLibraryApi);
  private readonly player = inject(PlayerService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.paramMap);
  private readonly albumTracks = signal<MusicTrack[]>([]);
  private readonly loadingError = signal('');
  private albumRequestId = 0;

  readonly isAdmin = this.auth.isAdmin;
  readonly isMetadataDialogOpen = signal(false);
  private lastChangeCount = this.albumActions.changeCount();
  private readonly refreshedInformation = signal<{
    key: string;
    imageUrl: string | null;
    musicBrainzId: string;
    updatedAt: string;
  } | null>(null);
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
    const key = `${artist.toLowerCase()}\0${selectedTrack.album.toLowerCase()}`;
    const refreshed = this.refreshedInformation()?.key === key ? this.refreshedInformation() : null;
    return {
      title: selectedTrack.album,
      artist,
      coverUrl: refreshed?.imageUrl || selectedTrack.coverUrl,
      musicBrainzId: refreshed?.musicBrainzId ?? '',
      informationUpdatedAt: refreshed?.updatedAt ?? '',
      releaseYear: albumTracks.find((track) => track.year > 0)?.year,
      tracks: albumTracks,
      totalDurationSeconds: albumTracks.reduce((total, track) => total + track.durationSeconds, 0),
    };
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
    if (!Number.isInteger(trackId) || trackId < 1) {
      this.isLoading.set(false);
      return;
    }

    try {
      const tracks = await this.libraryApi.listAlbumTracksByTrackId(trackId);
      if (requestId === this.albumRequestId) this.albumTracks.set(tracks);
    } catch {
      if (requestId === this.albumRequestId) {
        this.loadingError.set('The album could not be loaded. Check the music server connection.');
      }
    } finally {
      if (requestId === this.albumRequestId) this.isLoading.set(false);
    }
  }

  playTrack(tracks: MusicTrack[], index: number): void {
    this.player.play(tracks, index);
  }

  refreshInformation(): void {
    this.isMetadataDialogOpen.set(true);
  }

  closeMetadataDialog(): void {
    this.isMetadataDialogOpen.set(false);
  }

  saveRefreshedInformation(
    artist: string,
    album: string,
    selection: { result: MetadataRefreshResult; providerId: string },
  ): void {
    const { result, providerId } = selection;
    this.refreshedInformation.set({
      key: `${artist.toLowerCase()}\0${album.toLowerCase()}`,
      imageUrl: result.imageUrl,
      musicBrainzId: result.musicBrainzId,
      updatedAt: result.updatedAt,
    });
    this.toast.show(
      `Album information refreshed from ${providerId} at ${new Date(result.updatedAt).toLocaleString()}.`,
      'success',
    );
  }
}
