import {
  Component,
  ElementRef,
  inject,
  signal,
  ViewChild,
} from '@angular/core';
import { LibraryStore, LibraryView } from '@shared/services/library-store';
import { AlbumsPage } from './albums-page/albums-page';
import { ArtistsPage } from './artists-page/artists-page';
import { FoldersPage } from './folders-page/folders-page';
import { TracksPage } from './tracks-page/tracks-page';

@Component({
  selector: 'app-library-page',
  imports: [TracksPage, AlbumsPage, ArtistsPage, FoldersPage],
  templateUrl: './library-page.html',
})
export class LibraryPage {
  private readonly tabViews: LibraryView[] = ['albums', 'artists', 'tracks', 'folders'];
  private readonly store = inject(LibraryStore);
  readonly collectionView = this.store.collectionView;
  readonly searchQuery = this.store.searchQuery;
  readonly refreshTrigger = signal(0);
  readonly tabPanelMinHeight = signal(0);
  readonly tabTransition = signal<{
    from: LibraryView;
    to: LibraryView;
    direction: 'left' | 'right';
  } | null>(null);

  @ViewChild('collectionPanelContainer')
  private collectionPanelContainer?: ElementRef<HTMLElement>;

  selectTab(view: LibraryView): void {
    const currentView = this.collectionView();
    if (view === currentView) return;
    const panel = this.collectionPanelContainer?.nativeElement;
    if (panel) {
      const panelTop = panel.getBoundingClientRect().top + window.scrollY;
      const minimumHeight = Math.max(0, window.scrollY + window.innerHeight - panelTop);
      this.tabPanelMinHeight.update((height) => Math.max(height, minimumHeight));
    }
    this.tabTransition.set({
      from: currentView,
      to: view,
      direction:
        this.tabViews.indexOf(view) > this.tabViews.indexOf(currentView) ? 'right' : 'left',
    });
    this.store.collectionView.set(view);
    this.store.currentPage.set(this.store.currentPages()[view]);
  }

  finishTabTransition(view: LibraryView, event: AnimationEvent): void {
    if (
      event.target === event.currentTarget &&
      this.tabTransition()?.from === view
    ) {
      this.tabTransition.set(null);
    }
  }

  onTabKeydown(event: KeyboardEvent, view: LibraryView): void {
    const views: LibraryView[] = ['albums', 'artists', 'tracks', 'folders'];
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
}
