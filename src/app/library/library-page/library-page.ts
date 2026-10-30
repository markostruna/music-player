import {
  AfterViewInit,
  Component,
  computed,
  inject,
  OnDestroy,
  OnInit,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthState } from '@shared/services/auth-state';
import { formatTime } from '@shared/utils/format-time';
import { LIBRARY_PAGE_SIZE, LibraryStore, LibraryView } from '@shared/services/library-store';
import {
  AlbumSummary,
  MetadataPatch,
  MusicLibraryApi,
  MusicTrack,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { ToastService } from '@shared/services/toast-service';
import { IdentifyAlbumDialog, IdentifyAlbumRequest } from '../identify-album-dialog/identify-album-dialog';
import { MusicShellState } from '../../shell/music-shell/music-shell-state';

@Component({
  selector: 'app-library-page',
  imports: [FormsModule, RouterLink, IdentifyAlbumDialog],
  templateUrl: './library-page.html',
})
export class LibraryPage implements OnInit, AfterViewInit, OnDestroy {
  private readonly auth = inject(AuthState);
  private readonly library = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  private readonly shellState = inject(MusicShellState);
  private readonly toast = inject(ToastService);

  readonly identifyRequest = signal<IdentifyAlbumRequest | null>(null);
  readonly albumMenu = signal<{ album: AlbumSummary; left: number; top: number } | null>(null);

  readonly activeSection = this.shellState.activeSection;
  readonly searchQuery = this.store.searchQuery;
  readonly collectionView = this.store.collectionView;
  readonly currentPage = this.store.currentPage;
  readonly pagedTracks = this.store.pageTracks;
  readonly pageSize = LIBRARY_PAGE_SIZE;
  readonly isPlaying = this.player.isPlaying;
  readonly isLoading = this.store.isLoading;
  private readonly pageError = signal('');
  readonly errorMessage = computed(() => this.store.errorMessage() || this.pageError());
  readonly currentTrack = this.player.currentTrack;
  readonly isAdmin = this.auth.isAdmin;
  readonly selectedTrackIds = signal(new Set<number>());
  readonly hasSelection = computed(() => this.selectedTrackIds().size > 0);
  readonly totalItems = computed(() => {
    switch (this.collectionView()) {
      case 'tracks':
        return this.store.totalTrackCount();
      case 'albums':
        return this.store.totalAlbumCount();
      case 'artists':
        return this.store.totalArtistCount();
      default:
        return 0;
    }
  });
  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalItems() / this.pageSize)));
  readonly pageNumber = computed(() => Math.min(this.currentPage(), this.totalPages()));
  readonly pageStart = computed(() =>
    this.totalItems() === 0 ? 0 : (this.pageNumber() - 1) * this.pageSize + 1,
  );
  readonly pageEnd = computed(() => Math.min(this.pageNumber() * this.pageSize, this.totalItems()));
  readonly visibleTracks = computed(() => this.pagedTracks());
  readonly visibleAlbums = this.store.pageAlbums;
  readonly visibleArtists = this.store.pageArtists;
  metadata: MetadataPatch = {};
  destinationFolderId: number | null = null;
  folders: Awaited<ReturnType<MusicLibraryApi['listFolders']>> = [];
  operationMessage = '';

  readonly formatTime = formatTime;

  ngOnInit(): void {
    void this.loadLibrary();
  }

  ngAfterViewInit(): void {
    window.scrollTo(0, this.store.scrollTop);
  }

  ngOnDestroy(): void {
    this.store.scrollTop = window.scrollY;
  }

  async loadLibrary(): Promise<void> {
    this.pageError.set('');
    const view = this.collectionView();
    if (view === 'tracks') {
      await this.store.loadTrackPage(this.searchQuery(), this.currentPage(), this.pageSize);
    } else {
      await this.store.loadCollectionPage(
        view,
        this.searchQuery(),
        this.currentPage(),
        this.pageSize,
      );
    }
    if (this.isAdmin() && !this.store.errorMessage()) {
      try {
        this.folders = await this.library.listFolders();
      } catch {
        this.pageError.set('The music folders could not be loaded.');
      }
    }
  }
  setCollectionView(view: LibraryView): void {
    this.collectionView.set(view);
    this.currentPage.set(1);
    this.selectedTrackIds.set(new Set<number>());
    if (view === 'tracks') {
      void this.store.loadTrackPage(this.searchQuery(), 1, this.pageSize);
    } else {
      void this.store.loadCollectionPage(view, this.searchQuery(), 1, this.pageSize);
    }
  }

  setSearchQuery(query: string): void {
    this.searchQuery.set(query);
    this.currentPage.set(1);
    const view = this.collectionView();
    if (view === 'tracks') {
      void this.store.loadTrackPage(query, 1, this.pageSize);
    } else {
      void this.store.loadCollectionPage(view, query, 1, this.pageSize);
    }
  }

  previousPage(): void {
    const page = Math.max(1, this.currentPage() - 1);
    this.currentPage.set(page);
    const view = this.collectionView();
    if (view === 'tracks') {
      void this.store.loadTrackPage(this.searchQuery(), page, this.pageSize);
    } else {
      void this.store.loadCollectionPage(view, this.searchQuery(), page, this.pageSize);
    }
  }

  nextPage(): void {
    const page = Math.min(this.totalPages(), this.currentPage() + 1);
    this.currentPage.set(page);
    const view = this.collectionView();
    if (view === 'tracks') {
      void this.store.loadTrackPage(this.searchQuery(), page, this.pageSize);
    } else {
      void this.store.loadCollectionPage(view, this.searchQuery(), page, this.pageSize);
    }
  }

  playGroup(tracks: MusicTrack[]): void {
    this.player.play(tracks, 0);
  }

  openAlbumMenu(album: AlbumSummary, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (this.albumMenu()?.album.coverTrackId === album.coverTrackId) {
      this.albumMenu.set(null);
      return;
    }
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const menuWidth = 160;
    const menuHeight = 130;
    this.albumMenu.set({
      album,
      left: Math.max(8, Math.min(rect.left, window.innerWidth - menuWidth - 8)),
      top:
        rect.bottom + menuHeight > window.innerHeight
          ? Math.max(8, rect.top - menuHeight)
          : rect.bottom + 4,
    });
  }

  closeAlbumMenu(): void {
    this.albumMenu.set(null);
  }

  async playAlbumFromMenu(): Promise<void> {
    const menu = this.albumMenu();
    this.closeAlbumMenu();
    if (!menu) return;
    try {
      this.player.play(await this.library.listAlbumTracks(menu.album.artist, menu.album.album), 0);
    } catch {
      this.toast.show('The album tracks could not be loaded.', 'error');
    }
  }

  async enqueueAlbumFromMenu(): Promise<void> {
    const menu = this.albumMenu();
    this.closeAlbumMenu();
    if (!menu) return;
    try {
      this.player.enqueue(await this.library.listAlbumTracks(menu.album.artist, menu.album.album));
      this.toast.show(`Added ${menu.album.album} to the queue.`, 'success');
    } catch {
      this.toast.show('The album tracks could not be loaded.', 'error');
    }
  }

  async identifyAlbumFromMenu(): Promise<void> {
    const menu = this.albumMenu();
    this.closeAlbumMenu();
    if (!menu) return;
    try {
      this.identifyRequest.set({
        title: menu.album.album,
        artist: menu.album.artist,
        tracks: await this.library.listAlbumTracks(menu.album.artist, menu.album.album),
      });
    } catch {
      this.toast.show('The album tracks could not be loaded.', 'error');
    }
  }

  async closeIdentify(changed: boolean): Promise<void> {
    this.identifyRequest.set(null);
    if (changed) await this.store.refresh();
  }
  async chooseTrack(track: MusicTrack): Promise<void> {
    try {
      const queue = await this.library.searchTracks(this.searchQuery());
      this.player.play(queue, queue.findIndex((candidate) => candidate.id === track.id));
    } catch {
      this.toast.show('The selected track could not be loaded for playback.', 'error');
    }
  }

  async playFirstTrack(): Promise<void> {
    try {
      const tracks =
        this.collectionView() === 'tracks'
          ? await this.library.searchTracks(this.searchQuery())
          : await this.library.searchTracks('');
      this.player.play(tracks, 0);
    } catch {
      this.toast.show('Tracks could not be loaded for playback.', 'error');
    }
  }
  toggleSelection(trackId: number, event: Event): void {
    const selected = new Set(this.selectedTrackIds());
    if ((event.target as HTMLInputElement).checked) selected.add(trackId);
    else selected.delete(trackId);
    this.selectedTrackIds.set(selected);
    this.operationMessage = '';
  }

  async saveMetadata(): Promise<void> {
    const ids = [...this.selectedTrackIds()];
    const changes = Object.fromEntries(
      Object.entries(this.metadata).filter(([, value]) => value !== '' && value !== undefined),
    );
    if (ids.length === 0 || Object.keys(changes).length === 0) return;
    try {
      const results = await this.library.updateMetadata(ids, changes);
      const succeeded = results.filter((result) => result.succeeded).length;
      this.operationMessage = `Updated tags for ${succeeded} of ${results.length} selected tracks.`;
      this.metadata = {};
      await this.loadLibrary();
    } catch {
      this.operationMessage = 'The tag update could not be completed.';
    }
  }

  async updateCover(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    const ids = [...this.selectedTrackIds()];
    if (!file || ids.length === 0) return;
    try {
      const results = await this.library.updateCover(ids, file);
      const succeeded = results.filter((result) => result.succeeded).length;
      this.operationMessage = `Updated cover art for ${succeeded} of ${results.length} selected tracks.`;
      await this.loadLibrary();
    } catch {
      this.operationMessage =
        'The image could not be embedded. Use a PNG, JPEG, GIF, or WebP image under 10 MB.';
    } finally {
      input.value = '';
    }
  }

  setDestinationFolder(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.destinationFolderId = value ? Number(value) : null;
  }

  async moveSelected(): Promise<void> {
    if (!this.destinationFolderId) return;
    try {
      const results = await this.library.moveTracks(
        [...this.selectedTrackIds()],
        this.destinationFolderId,
      );
      const succeeded = results.filter((result) => result.succeeded).length;
      this.operationMessage = `Moved ${succeeded} of ${results.length} selected tracks.`;
      await this.loadLibrary();
    } catch {
      this.operationMessage = 'The selected tracks could not be moved.';
    }
  }
}
