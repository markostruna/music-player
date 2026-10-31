import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthState } from '@shared/services/auth-state';
import { formatTime } from '@shared/utils/format-time';
import { LibraryStore } from '@shared/services/library-store';
import {
  MetadataRefreshResult,
  MusicLibraryApi,
  MusicTrack,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { ToastService } from '@shared/services/toast-service';
import { MetadataRefreshDialog } from '../metadata-refresh-dialog/metadata-refresh-dialog';
import { distinctUntilChanged, map } from 'rxjs';

@Component({
  selector: 'app-album-page',
  imports: [MetadataRefreshDialog, RouterLink],
  templateUrl: './album-page.html',
})
export class AlbumPage {
  private readonly auth = inject(AuthState);
  private readonly store = inject(LibraryStore);
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
  readonly editingDescriptionKey = signal<string | null>(null);
  readonly isCoverDialogOpen = signal(false);
  readonly coverPreviewUrl = signal('');
  readonly coverError = signal('');
  readonly isSavingCover = signal(false);
  private readonly selectedCover = signal<File | null>(null);
  private readonly maxCoverSize = 10 * 1024 * 1024;
  private readonly refreshedInformation = signal<{
    key: string;
    description: string;
    imageUrl: string | null;
    musicBrainzId: string;
    updatedAt: string;
  } | null>(null);
  descriptionDraft: string | null = null;
  private descriptionDraftKey: string | null = null;
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
      description: refreshed?.description ?? selectedTrack.albumDescription,
      musicBrainzId: refreshed?.musicBrainzId ?? '',
      informationUpdatedAt: refreshed?.updatedAt ?? '',
      releaseYear: albumTracks.find((track) => track.year > 0)?.year,
      tracks: albumTracks,
      totalDurationSeconds: albumTracks.reduce((total, track) => total + track.durationSeconds, 0),
    };
  });

  constructor() {
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

  descriptionValue(artist: string, album: string, description: string): string {
    return this.descriptionDraftKey === `${artist}\0${album}`
      ? (this.descriptionDraft ?? '')
      : description;
  }

  isEditingDescription(artist: string, album: string): boolean {
    return this.editingDescriptionKey() === `${artist}\0${album}`;
  }

  editDescription(artist: string, album: string, description: string): void {
    const key = `${artist}\0${album}`;
    this.descriptionDraftKey = key;
    this.descriptionDraft = description;
    this.editingDescriptionKey.set(key);
  }

  setDescription(event: Event, artist: string, album: string): void {
    this.descriptionDraftKey = `${artist}\0${album}`;
    this.descriptionDraft = (event.target as HTMLTextAreaElement).value;
  }

  cancelDescriptionEdit(): void {
    this.descriptionDraft = null;
    this.descriptionDraftKey = null;
    this.editingDescriptionKey.set(null);
  }

  async saveDescription(artist: string, album: string): Promise<void> {
    const key = `${artist}\0${album}`;
    try {
      const description =
        this.descriptionDraftKey === key
          ? (this.descriptionDraft ?? '')
          : (this.album()?.description ?? '');
      await this.libraryApi.updateDescription(artist, album, description);
      this.descriptionDraft = null;
      this.descriptionDraftKey = null;
      this.editingDescriptionKey.set(null);
      await this.store.refreshTracks();
      await this.loadAlbum(Number(this.params()?.get('trackId')));
      this.toast.show('Album description saved.', 'success');
    } catch {
      this.toast.show('The album description could not be saved.', 'error');
    }
  }

  openCoverDialog(): void {
    this.coverError.set('');
    this.isCoverDialogOpen.set(true);
  }

  closeCoverDialog(): void {
    if (this.isSavingCover()) return;
    this.clearCoverSelection();
    this.coverError.set('');
    this.isCoverDialogOpen.set(false);
  }

  selectCover(event: Event): void {
    const input = event.target as HTMLInputElement;
    const image = input.files?.[0];
    input.value = '';
    if (!image) return;

    this.clearCoverSelection();
    if (!['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(image.type)) {
      this.coverError.set('Choose a PNG, JPEG, GIF, or WebP image.');
      return;
    }
    if (image.size > this.maxCoverSize) {
      this.coverError.set('Choose an image under 10 MB.');
      return;
    }

    this.selectedCover.set(image);
    this.coverPreviewUrl.set(URL.createObjectURL(image));
    this.coverError.set('');
  }

  async saveCover(artist: string, album: string): Promise<void> {
    const image = this.selectedCover();
    if (!image || this.isSavingCover()) return;
    this.isSavingCover.set(true);
    this.coverError.set('');
    try {
      await this.libraryApi.updateEntityImage(artist, album, image);
      await this.store.refreshTracks();
      await this.loadAlbum(Number(this.params()?.get('trackId')));
      this.toast.show('Album artwork saved.', 'success');
      this.clearCoverSelection();
      this.isCoverDialogOpen.set(false);
    } catch {
      this.coverError.set('The album artwork could not be saved. Please try again.');
    } finally {
      this.isSavingCover.set(false);
    }
  }

  private clearCoverSelection(): void {
    const previewUrl = this.coverPreviewUrl();
    if (previewUrl) URL.revokeObjectURL(previewUrl);
    this.coverPreviewUrl.set('');
    this.selectedCover.set(null);
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
    this.descriptionDraft = null;
    this.descriptionDraftKey = null;
    this.editingDescriptionKey.set(null);
    this.refreshedInformation.set({
      key: `${artist.toLowerCase()}\0${album.toLowerCase()}`,
      description: result.description,
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
