import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthState } from '@shared/services/auth-state';
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
  selector: 'app-artist-page',
  imports: [MetadataRefreshDialog, RouterLink],
  templateUrl: './artist-page.html',
})
export class ArtistPage {
  private readonly auth = inject(AuthState);
  private readonly store = inject(LibraryStore);
  private readonly libraryApi = inject(MusicLibraryApi);
  private readonly player = inject(PlayerService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.paramMap);
  private readonly artistTracks = signal<MusicTrack[]>([]);
  private readonly loadingError = signal('');
  private artistRequestId = 0;

  readonly isAdmin = this.auth.isAdmin;
  readonly isMetadataDialogOpen = signal(false);
  readonly editingDescriptionArtist = signal<string | null>(null);
  readonly isPictureDialogOpen = signal(false);
  readonly picturePreviewUrl = signal('');
  readonly pictureError = signal('');
  readonly isSavingPicture = signal(false);
  private readonly selectedPicture = signal<File | null>(null);
  private readonly maxPictureSize = 10 * 1024 * 1024;
  private readonly refreshedInformation = signal<{
    artist: string;
    description: string;
    imageUrl: string | null;
    imageMissing: boolean;
    musicBrainzId: string;
    updatedAt: string;
  } | null>(null);
  descriptionDraft: string | null = null;
  private descriptionDraftArtist: string | null = null;
  readonly isLoading = signal(true);
  readonly errorMessage = computed(() => {
    if (this.loadingError()) return this.loadingError();
    return !this.isLoading() && !this.artist()
      ? 'This artist could not be found in your library.'
      : '';
  });
  readonly artist = computed(() => {
    const name = (this.params()?.get('name') ?? '').trim().toLowerCase();
    const tracks = this.artistTracks()
      .filter(
        (track) =>
          (track.artist.trim() || 'Unknown artist').toLowerCase() === name ||
          (track.albumArtist.trim() || track.artist).toLowerCase() === name,
      );
    if (!tracks.length) return null;

    const groups = new Map<
      string,
      { key: string; title: string; coverUrl: string; year: number; tracks: MusicTrack[] }
    >();
    for (const track of tracks) {
      const key = `${(track.albumArtist.trim() || track.artist).toLowerCase()}\u0000${track.album.toLowerCase()}`;
      const group = groups.get(key);
      if (group) {
        group.tracks.push(track);
        group.year ||= track.year;
      } else {
        groups.set(key, {
          key,
          title: track.album,
          coverUrl: track.coverUrl,
          year: track.year,
          tracks: [track],
        });
      }
    }

    const first = tracks[0];
    const displayName =
      [first.artist, first.albumArtist]
        .find((value) => value.trim().toLowerCase() === name)
        ?.trim() ?? name;
    const isTrackArtist = (first.artist.trim() || 'Unknown artist').toLowerCase() === name;
    const refreshed =
      this.refreshedInformation()?.artist === name ? this.refreshedInformation() : null;
    return {
      name: displayName,
      coverUrl:
        refreshed?.imageUrl ||
        (isTrackArtist
          ? first.artistImageUrl || first.coverUrl
          : first.albumArtistImageUrl || first.coverUrl),
      imageMissing:
        refreshed?.imageMissing ??
        (isTrackArtist ? first.artistImageMissing : first.albumArtistImageMissing),
      description:
        refreshed?.description ??
        (isTrackArtist ? first.artistDescription : first.albumArtistDescription),
      musicBrainzId: refreshed?.musicBrainzId ?? '',
      informationUpdatedAt: refreshed?.updatedAt ?? '',
      trackCount: tracks.length,
      albums: [...groups.values()].sort(
        (a, b) => b.year - a.year || a.title.localeCompare(b.title),
      ),
    };
  });

  constructor() {
    this.route.paramMap
      .pipe(
        map((params) => params.get('name') ?? ''),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((artist) => void this.loadArtist(artist));
  }

  private async loadArtist(artist: string): Promise<void> {
    const requestId = ++this.artistRequestId;
    this.isLoading.set(true);
    this.loadingError.set('');
    this.artistTracks.set([]);
    try {
      const tracks = await this.libraryApi.listArtistTracks(artist);
      if (requestId === this.artistRequestId) this.artistTracks.set(tracks);
    } catch {
      if (requestId === this.artistRequestId) {
        this.loadingError.set('The artist could not be loaded. Check the music server connection.');
      }
    } finally {
      if (requestId === this.artistRequestId) this.isLoading.set(false);
    }
  }

  descriptionValue(artist: string, description: string): string {
    return this.descriptionDraftArtist === artist ? (this.descriptionDraft ?? '') : description;
  }

  isEditingDescription(artist: string): boolean {
    return this.editingDescriptionArtist() === artist;
  }

  editDescription(artist: string, description: string): void {
    this.descriptionDraftArtist = artist;
    this.descriptionDraft = description;
    this.editingDescriptionArtist.set(artist);
  }

  setDescription(event: Event, artist: string): void {
    this.descriptionDraftArtist = artist;
    this.descriptionDraft = (event.target as HTMLTextAreaElement).value;
  }

  cancelDescriptionEdit(): void {
    this.descriptionDraft = null;
    this.descriptionDraftArtist = null;
    this.editingDescriptionArtist.set(null);
  }

  async saveDescription(artist: string): Promise<void> {
    try {
      const description =
        this.descriptionDraftArtist === artist
          ? (this.descriptionDraft ?? '')
          : (this.artist()?.description ?? '');
      await this.libraryApi.updateDescription(artist, null, description);
      this.descriptionDraft = null;
      this.descriptionDraftArtist = null;
      this.editingDescriptionArtist.set(null);
      await this.store.refreshTracks();
      await this.loadArtist(artist);
      this.toast.show('Artist description saved.', 'success');
    } catch {
      this.toast.show('The artist description could not be saved.', 'error');
    }
  }

  openPictureDialog(): void {
    this.pictureError.set('');
    this.isPictureDialogOpen.set(true);
  }

  closePictureDialog(): void {
    if (this.isSavingPicture()) return;
    this.clearPictureSelection();
    this.pictureError.set('');
    this.isPictureDialogOpen.set(false);
  }

  selectPicture(event: Event): void {
    const input = event.target as HTMLInputElement;
    const image = input.files?.[0];
    input.value = '';
    if (!image) return;

    this.clearPictureSelection();
    if (!['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(image.type)) {
      this.pictureError.set('Choose a PNG, JPEG, GIF, or WebP image.');
      return;
    }
    if (image.size > this.maxPictureSize) {
      this.pictureError.set('Choose an image under 10 MB.');
      return;
    }

    this.selectedPicture.set(image);
    this.picturePreviewUrl.set(URL.createObjectURL(image));
    this.pictureError.set('');
  }

  async savePicture(artist: string): Promise<void> {
    const image = this.selectedPicture();
    if (!image || this.isSavingPicture()) return;
    this.isSavingPicture.set(true);
    this.pictureError.set('');
    try {
      await this.libraryApi.updateEntityImage(artist, null, image);
      await this.store.refreshTracks();
      await this.loadArtist(artist);
      this.toast.show('Artist picture saved.', 'success');
      this.clearPictureSelection();
      this.isPictureDialogOpen.set(false);
    } catch {
      this.pictureError.set('The artist picture could not be saved. Please try again.');
    } finally {
      this.isSavingPicture.set(false);
    }
  }

  private clearPictureSelection(): void {
    const previewUrl = this.picturePreviewUrl();
    if (previewUrl) URL.revokeObjectURL(previewUrl);
    this.picturePreviewUrl.set('');
    this.selectedPicture.set(null);
  }

  refreshInformation(): void {
    this.isMetadataDialogOpen.set(true);
  }

  closeMetadataDialog(): void {
    this.isMetadataDialogOpen.set(false);
  }

  saveRefreshedInformation(
    artist: string,
    selection: { result: MetadataRefreshResult; providerId: string },
  ): void {
    const { result, providerId } = selection;
    this.descriptionDraft = null;
    this.descriptionDraftArtist = null;
    this.editingDescriptionArtist.set(null);
    this.refreshedInformation.set({
      artist: artist.toLowerCase(),
      description: result.description,
      imageUrl: result.imageUrl,
      imageMissing: result.imageMissing,
      musicBrainzId: result.musicBrainzId,
      updatedAt: result.updatedAt,
    });
    this.toast.show(
      `Artist information refreshed from ${providerId}. ${this.pictureRefreshStatus(result)} Updated ${new Date(result.updatedAt).toLocaleString()}.`,
      'success',
    );
  }

  private pictureRefreshStatus(
    result: Awaited<ReturnType<MusicLibraryApi['refreshInformation']>>,
  ): string {
    if (result.imageUpdated && result.imageProvider === 'Fanart.tv')
      return 'Artist picture updated from Fanart.tv.';
    if (result.imageUpdated && result.imageProvider) {
      return `Fanart.tv returned no picture; updated the image from ${result.imageProvider} instead.`;
    }
    return result.imageMissing
      ? 'Fanart.tv and linked sources returned no picture; the artist picture remains missing.'
      : 'No new picture was returned; the existing artist picture was kept.';
  }
}
