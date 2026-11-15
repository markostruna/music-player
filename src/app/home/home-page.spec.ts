import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LibraryOperationTracker } from '@shared/services/library-operation-tracker';
import { LibraryStore } from '@shared/services/library-store';
import { AlbumSummary, LibraryOperationStatus, MusicLibraryApi } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { HomePage } from './home-page';

describe('HomePage recently added albums', () => {
  it('renders the seven returned albums and refreshes after a library operation', async () => {
    const albums: AlbumSummary[] = Array.from({ length: 7 }, (_, index) => ({
      album: `Album ${index}`,
      artist: 'Artist',
      releaseYear: null,
      trackCount: 1,
      durationSeconds: 100,
      coverTrackId: index + 1,
      coverUrl: '',
      fileExtensions: ['flac'],
      importedAt: '2026-01-01T00:00:00Z',
    }));
    const operation = signal<LibraryOperationStatus | null>(null);
    let recentCalls = 0;
    TestBed.configureTestingModule({
      imports: [HomePage],
      providers: [
        provideRouter([]),
        { provide: LibraryStore, useValue: { collectionView: signal('albums') } },
        { provide: PlayerService, useValue: {} },
        { provide: LibraryOperationTracker, useValue: { operation } },
        {
          provide: MusicLibraryApi,
          useValue: {
            listRecentAlbums: async () => { recentCalls++; return albums; },
            listAlbumPage: async () => ({ items: [] }),
            listArtistPage: async () => ({ items: [] }),
            listTrackPage: async () => ({ items: [] }),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(HomePage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const section = fixture.nativeElement.querySelector('[aria-labelledby="home-recent-heading"]') as HTMLElement;
    expect(section.querySelectorAll('app-album-tile').length).toBe(7);
    expect(section.textContent).toContain('Added recently');
    expect(section.textContent).toContain('Album 0');

    operation.set({
      id: '1', sourceRootId: 1, rootName: 'Music', operationType: 'scan',
      state: 'completed', message: '', completedUnits: 1, totalUnits: 1,
      startedAt: '', finishedAt: '',
    });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(recentCalls).toBe(2);
  });
});
