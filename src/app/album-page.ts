import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthState, ThemeName } from './auth-state';
import { formatTime } from './format-time';
import { LibraryStore } from './library-store';
import { MusicLibraryApi, MusicTrack } from './music-library-api';
import { PlayerService } from './player.service';

@Component({
  selector: 'app-album-page',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './album-page.html',
})
export class AlbumPage implements OnInit {
  private readonly auth = inject(AuthState);
  private readonly router = inject(Router);
  private readonly store = inject(LibraryStore);
  private readonly libraryApi = inject(MusicLibraryApi);
  private readonly player = inject(PlayerService);
  private readonly params = toSignal(inject(ActivatedRoute).paramMap);

  readonly isAdmin = this.auth.isAdmin;
  readonly user = this.auth.user;
  readonly themeOptions: ThemeName[] = ['Light', 'Dark', 'Blue'];
  readonly themeError = signal('');
  readonly metadataMessage = signal('');
  readonly isRefreshingInformation = signal(false);
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
  readonly isLoading = this.store.isLoading;
  readonly errorMessage = computed(() => {
    if (this.store.errorMessage()) return this.store.errorMessage();
    return !this.store.isLoading() && !this.album() ? 'This album could not be found in your library.' : '';
  });
  readonly currentTrack = this.player.currentTrack;
  readonly isPlaying = this.player.isPlaying;
  readonly formatTime = formatTime;
  readonly album = computed(() => {
    const trackId = Number(this.params()?.get('trackId'));
    const selectedTrack = this.store.tracks().find((track) => track.id === trackId);
    if (!selectedTrack) return null;

    const artist = selectedTrack.albumArtist.trim() || selectedTrack.artist;
    const albumTracks = this.store.tracks()
      .filter((track) =>
        track.album.toLowerCase() === selectedTrack.album.toLowerCase()
        && (track.albumArtist.trim() || track.artist).toLowerCase() === artist.toLowerCase())
      .sort((first, second) =>
        first.discNumber - second.discNumber
        || first.trackNumber - second.trackNumber
        || first.title.localeCompare(second.title));

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
    };
  });

  ngOnInit(): void {
    void this.store.ensureLoaded();
  }

  playTrack(tracks: MusicTrack[], index: number): void {
    this.player.play(tracks, index);
  }

  descriptionValue(artist: string, album: string, description: string): string {
    return this.descriptionDraftKey === `${artist}\0${album}` ? this.descriptionDraft ?? '' : description;
  }

  setDescription(event: Event, artist: string, album: string): void {
    this.descriptionDraftKey = `${artist}\0${album}`;
    this.descriptionDraft = (event.target as HTMLTextAreaElement).value;
  }

  async saveDescription(artist: string, album: string): Promise<void> {
    const key = `${artist}\0${album}`;
    try {
      const description = this.descriptionDraftKey === key ? this.descriptionDraft ?? '' : this.album()?.description ?? '';
      await this.libraryApi.updateDescription(artist, album, description);
      this.descriptionDraft = null;
      this.descriptionDraftKey = null;
      await this.store.refresh();
      this.metadataMessage.set('Album description saved.');
    } catch {
      this.metadataMessage.set('The album description could not be saved.');
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
      await this.store.refresh();
      this.metadataMessage.set('Album artwork saved.');
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

  async refreshInformation(artist: string, album: string): Promise<void> {
    this.isRefreshingInformation.set(true);
    this.metadataMessage.set('');
    try {
      const result = await this.libraryApi.refreshInformation(artist, album);
      if (!result.found) {
        this.metadataMessage.set('No matching album record was found. Existing information was left unchanged.');
        return;
      }
      this.descriptionDraft = null;
      this.descriptionDraftKey = null;
      this.refreshedInformation.set({
        key: `${artist.toLowerCase()}\0${album.toLowerCase()}`,
        description: result.description,
        imageUrl: result.imageUrl,
        musicBrainzId: result.musicBrainzId,
        updatedAt: result.updatedAt,
      });
      this.metadataMessage.set(
        `Album information refreshed from MusicBrainz (${result.musicBrainzId}) at ${new Date(result.updatedAt).toLocaleString()}.`,
      );
    } catch {
      this.metadataMessage.set('Album information could not be refreshed. Please try again later.');
    } finally {
      this.isRefreshingInformation.set(false);
    }
  }

  async changeTheme(event: Event): Promise<void> {
    try {
      await this.auth.setTheme((event.target as HTMLSelectElement).value as ThemeName);
      this.themeError.set('');
    } catch {
      this.themeError.set('The theme preference could not be saved.');
    }
  }

  async signOut(): Promise<void> {
    this.player.stop();
    this.store.clear();
    await this.auth.signOut();
    void this.router.navigateByUrl('/login');
  }
}
