import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthState, ThemeName } from './auth-state';
import { LibraryStore } from './library-store';
import { MusicLibraryApi, MusicTrack } from './music-library-api';
import { PlayerService } from './player.service';

@Component({
  selector: 'app-artist-page',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './artist-page.html',
})
export class ArtistPage implements OnInit {
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
  readonly isLoading = this.store.isLoading;
  readonly errorMessage = computed(() => {
    if (this.store.errorMessage()) return this.store.errorMessage();
    return !this.store.isLoading() && !this.artist() ? 'This artist could not be found in your library.' : '';
  });
  readonly artist = computed(() => {
    const name = (this.params()?.get('name') ?? '').trim().toLowerCase();
    const tracks = this.store.tracks().filter((track) =>
      (track.artist.trim() || 'Unknown artist').toLowerCase() === name
      || (track.albumArtist.trim() || track.artist).toLowerCase() === name);
    if (!tracks.length) return null;

    const groups = new Map<string, { key: string; title: string; coverUrl: string; year: number; tracks: MusicTrack[] }>();
    for (const track of tracks) {
      const key = `${(track.albumArtist.trim() || track.artist).toLowerCase()}\u0000${track.album.toLowerCase()}`;
      const group = groups.get(key);
      if (group) {
        group.tracks.push(track);
        group.year ||= track.year;
      } else {
        groups.set(key, { key, title: track.album, coverUrl: track.coverUrl, year: track.year, tracks: [track] });
      }
    }

    const first = tracks[0];
    const displayName = [first.artist, first.albumArtist].find((value) => value.trim().toLowerCase() === name)?.trim() ?? name;
    const isTrackArtist = (first.artist.trim() || 'Unknown artist').toLowerCase() === name;
    const refreshed = this.refreshedInformation()?.artist === name ? this.refreshedInformation() : null;
    return {
      name: displayName,
      coverUrl: refreshed?.imageUrl || (isTrackArtist
        ? first.artistImageUrl || first.coverUrl
        : first.albumArtistImageUrl || first.coverUrl),
      imageMissing: refreshed?.imageMissing ?? (isTrackArtist ? first.artistImageMissing : first.albumArtistImageMissing),
      description: refreshed?.description ?? (isTrackArtist ? first.artistDescription : first.albumArtistDescription),
      musicBrainzId: refreshed?.musicBrainzId ?? '',
      informationUpdatedAt: refreshed?.updatedAt ?? '',
      trackCount: tracks.length,
      albums: [...groups.values()].sort((a, b) => b.year - a.year || a.title.localeCompare(b.title)),
    };
  });
  ngOnInit(): void {
    void this.store.ensureLoaded();
  }

  descriptionValue(artist: string, description: string): string {
    return this.descriptionDraftArtist === artist ? this.descriptionDraft ?? '' : description;
  }

  setDescription(event: Event, artist: string): void {
    this.descriptionDraftArtist = artist;
    this.descriptionDraft = (event.target as HTMLTextAreaElement).value;
  }

  async saveDescription(artist: string): Promise<void> {
    try {
      const description = this.descriptionDraftArtist === artist ? this.descriptionDraft ?? '' : this.artist()?.description ?? '';
      await this.libraryApi.updateDescription(artist, null, description);
      this.descriptionDraft = null;
      this.descriptionDraftArtist = null;
      await this.store.refresh();
      this.metadataMessage.set('Artist description saved.');
    } catch {
      this.metadataMessage.set('The artist description could not be saved.');
    }
  }

  async updatePicture(event: Event, artist: string): Promise<void> {
    const input = event.target as HTMLInputElement;
    const image = input.files?.[0];
    if (!image) return;
    try {
      await this.libraryApi.updateEntityImage(artist, null, image);
      await this.store.refresh();
      this.metadataMessage.set('Artist picture saved.');
    } catch {
      this.metadataMessage.set('The artist picture could not be saved. Use a PNG, JPEG, GIF, or WebP image under 10 MB.');
    } finally {
      input.value = '';
    }
  }

  async refreshInformation(artist: string): Promise<void> {
    this.isRefreshingInformation.set(true);
    this.metadataMessage.set('');
    try {
      const result = await this.libraryApi.refreshInformation(artist, null);
      if (!result.found) {
        this.metadataMessage.set('No matching artist record was found. Existing information was left unchanged.');
        return;
      }
      this.descriptionDraft = null;
      this.descriptionDraftArtist = null;
      this.refreshedInformation.set({
        artist: artist.toLowerCase(),
        description: result.description,
        imageUrl: result.imageUrl,
        imageMissing: result.imageMissing,
        musicBrainzId: result.musicBrainzId,
        updatedAt: result.updatedAt,
      });
      this.metadataMessage.set(
        `Artist information refreshed from MusicBrainz (${result.musicBrainzId}). ${this.pictureRefreshStatus(result)} Updated ${new Date(result.updatedAt).toLocaleString()}.`,
      );
    } catch {
      this.metadataMessage.set('Artist information could not be refreshed. Please try again later.');
    } finally {
      this.isRefreshingInformation.set(false);
    }
  }

  private pictureRefreshStatus(result: Awaited<ReturnType<MusicLibraryApi['refreshInformation']>>): string {
    if (result.imageUpdated && result.imageProvider === 'Fanart.tv') return 'Artist picture updated from Fanart.tv.';
    if (result.imageUpdated && result.imageProvider) {
      return `Fanart.tv returned no picture; updated the image from ${result.imageProvider} instead.`;
    }
    return result.imageMissing
      ? 'Fanart.tv and linked sources returned no picture; the artist picture remains missing.'
      : 'No new picture was returned; the existing artist picture was kept.';
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
