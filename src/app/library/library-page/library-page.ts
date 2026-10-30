import { AfterViewInit, Component, inject, OnDestroy, signal } from '@angular/core';
import { LibraryStore, LibraryView } from '@shared/services/library-store';
import { MusicLibraryApi } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { ToastService } from '@shared/services/toast-service';
import { MusicShellState } from '../../shell/music-shell/music-shell-state';
import { AlbumsPage } from './albums-page/albums-page';
import { ArtistsPage } from './artists-page/artists-page';
import { TracksPage } from './tracks-page/tracks-page';

@Component({
  selector: 'app-library-page',
  imports: [TracksPage, AlbumsPage, ArtistsPage],
  templateUrl: './library-page.html',
})
export class LibraryPage implements AfterViewInit, OnDestroy {
  private readonly library = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  private readonly player = inject(PlayerService);
  private readonly toast = inject(ToastService);
  readonly activeSection = inject(MusicShellState).activeSection;
  readonly collectionView = this.store.collectionView;
  readonly searchQuery = this.store.searchQuery;
  readonly refreshTrigger = signal(0);

  ngAfterViewInit(): void {
    window.scrollTo(0, this.store.scrollTop);
  }

  ngOnDestroy(): void {
    this.store.scrollTop = window.scrollY;
  }

  selectTab(view: LibraryView): void {
    this.store.collectionView.set(view);
    this.store.currentPage.set(this.store.currentPages()[view]);
  }

  onTabKeydown(event: KeyboardEvent, view: LibraryView): void {
    const views: LibraryView[] = ['tracks', 'albums', 'artists'];
    const currentIndex = views.indexOf(view);
    let nextIndex: number;
    switch (event.key) {
      case 'ArrowRight':
        nextIndex = (currentIndex + 1) % views.length;
        break;
      case 'ArrowLeft':
        nextIndex = (currentIndex + views.length - 1) % views.length;
        break;
      case 'Home':
        nextIndex = 0;
        break;
      case 'End':
        nextIndex = views.length - 1;
        break;
      default:
        return;
    }
    event.preventDefault();
    const nextView = views[nextIndex];
    this.selectTab(nextView);
    document.getElementById(`library-tab-${nextView}`)?.focus();
  }

  refreshLibrary(): void {
    this.refreshTrigger.update((trigger) => trigger + 1);
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
}
