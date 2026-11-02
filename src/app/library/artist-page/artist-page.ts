import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { Location } from '@angular/common';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthState } from '@shared/services/auth-state';
import { LibraryStore } from '@shared/services/library-store';
import {
  MetadataRefreshResult,
  MusicLibraryApi,
  MusicTrack,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { ToastService } from '@shared/services/toast-service';
import { AlbumActions, AlbumRef } from '../album-actions/album-actions';
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
  readonly albumActions = inject(AlbumActions);
  private lastChangeCount = this.albumActions.changeCount();
  private readonly libraryApi = inject(MusicLibraryApi);
  private readonly player = inject(PlayerService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly params = toSignal(this.route.paramMap);
  private readonly artistTracks = signal<MusicTrack[]>([]);
  private readonly artistMetadata = signal<{
    artist: string;
    description: string;
    imageUrl: string | null;
    imageMissing: boolean;
    referencedArtists: string[];
  } | null>(null);
  private readonly loadingError = signal('');
  private artistRequestId = 0;

  readonly isAdmin = this.auth.isAdmin;
  readonly isMetadataDialogOpen = signal(false);
  readonly editingDescriptionArtist = signal<string | null>(null);
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
          (track.albumArtist.trim() || track.artist.trim() || 'Unknown artist').toLowerCase() ===
          name,
      );
    if (!tracks.length) return null;

    const groups = new Map<
      string,
      { key: string; title: string; coverUrl: string; year: number; tracks: MusicTrack[] }
    >();
    for (const track of tracks) {
      const key = `${(track.albumArtist.trim() || track.artist.trim() || 'Unknown artist').toLowerCase()}\u0000${track.album.toLowerCase()}`;
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
    const displayName = first.albumArtist.trim() || first.artist.trim() || 'Unknown artist';
    const refreshed =
      this.refreshedInformation()?.artist === name ? this.refreshedInformation() : null;
    const metadata =
      this.artistMetadata()?.artist === name ? this.artistMetadata() : null;
    return {
      name: displayName,
      coverUrl: refreshed?.imageUrl || metadata?.imageUrl || first.coverUrl,
      imageMissing: refreshed?.imageMissing ?? metadata?.imageMissing ?? true,
      description: refreshed?.description ?? metadata?.description ?? '',
      referencedArtists: metadata?.referencedArtists ?? [],
      musicBrainzId: refreshed?.musicBrainzId ?? '',
      informationUpdatedAt: refreshed?.updatedAt ?? '',
      trackCount: tracks.length,
      albums: [...groups.values()].sort(
        (a, b) => b.year - a.year || a.title.localeCompare(b.title),
      ),
    };
  });

  constructor() {
    effect(() => {
      const changeCount = this.albumActions.changeCount();
      if (changeCount === this.lastChangeCount) return;
      this.lastChangeCount = changeCount;
      const name = untracked(() => this.params()?.get('name'));
      if (name) untracked(() => void this.loadArtist(name));
    });
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
    this.artistMetadata.set(null);
    this.refreshedInformation.set(null);
    try {
      const [tracks, metadata] = await Promise.all([
        this.libraryApi.listArtistTracks(artist),
        this.libraryApi.getArtistMetadata(artist),
      ]);
      if (requestId === this.artistRequestId) {
        this.artistTracks.set(tracks);
        this.artistMetadata.set({
          artist: artist.toLowerCase(),
          description: metadata.description,
          imageUrl: metadata.imageUrl,
          imageMissing: metadata.imageMissing,
          referencedArtists: metadata.referencedArtists,
        });
      }
    } catch {
      if (requestId === this.artistRequestId) {
        this.loadingError.set('The artist could not be loaded. Check the music server connection.');
      }
    } finally {
      if (requestId === this.artistRequestId) this.isLoading.set(false);
    }
  }

  albumRef(album: { title: string; tracks: MusicTrack[] }): AlbumRef {
    const track = album.tracks[0];
    return { artist: track.albumArtist.trim() || track.artist.trim(), title: album.title };
  }

  descriptionValue(artist: string, description: string): string {
    return this.descriptionDraftArtist === artist ? (this.descriptionDraft ?? '') : description;
  }

  formatDescription(description: string, referencedArtists: string[]): string {
    const knownArtists = new Map(
      referencedArtists.map((artist) => [artist.toLocaleLowerCase(), artist]),
    );
    return description
      ? description
          .replace(/\[a=([^\]]+)\]/gi, (_reference, artist: string) => {
            const name = artist.trim();
            const knownArtist = knownArtists.get(name.toLocaleLowerCase());
            if (!knownArtist) return this.escapeHtml(name);
            const tree = this.router.createUrlTree(['/library/artist', knownArtist]);
            const href = this.location.prepareExternalUrl(this.router.serializeUrl(tree));
            return `<a href="${this.escapeHtml(href)}">${this.escapeHtml(name)}</a>`;
          })
          .replace(/\[b\]/gi, '<strong>')
          .replace(/\[\/b\]/gi, '</strong>')
          .replace(/\[i\]/gi, '<em>')
          .replace(/\[\/i\]/gi, '</em>')
          .replace(/\[s\]/gi, '<s>')
          .replace(/\[\/s\]/gi, '</s>')
      : 'No artist description is available.';
  }

  private escapeHtml(value: string): string {
    return value.replace(/[&<>"']/g, (character) => {
      switch (character) {
        case '&':
          return '&amp;';
        case '<':
          return '&lt;';
        case '>':
          return '&gt;';
        case '"':
          return '&quot;';
        default:
          return '&#39;';
      }
    });
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
      await this.libraryApi.updateArtistDescription(artist, description);
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

  refreshInformation(): void {
    this.isMetadataDialogOpen.set(true);
  }

  closeMetadataDialog(): void {
    this.isMetadataDialogOpen.set(false);
  }

  async saveRefreshedInformation(
    artist: string,
    selection: { result: MetadataRefreshResult; providerId: string },
  ): Promise<void> {
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
    try {
      await this.loadArtistMetadata(artist);
    } catch {
      this.toast.show('Artist reference links could not be loaded.', 'error');
    }
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

  private async loadArtistMetadata(artist: string): Promise<void> {
    const metadata = await this.libraryApi.getArtistMetadata(artist);
    if (this.artistMetadata()?.artist !== artist.toLowerCase()) return;
    this.artistMetadata.set({
      artist: artist.toLowerCase(),
      description: metadata.description,
      imageUrl: metadata.imageUrl,
      imageMissing: metadata.imageMissing,
      referencedArtists: metadata.referencedArtists,
    });
  }
}
