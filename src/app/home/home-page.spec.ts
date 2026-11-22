import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LibraryOperationTracker } from '@shared/services/library-operation-tracker';
import { LibraryStore } from '@shared/services/library-store';
import { AlbumSummary, LibraryOperationStatus, MusicLibraryApi } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { PlaybackSyncApi } from '@shared/services/playback-sync-api';
import { PlaylistService } from '@shared/services/playlist.service';
import { HomePage } from './home-page';

describe('HomePage recently added albums', () => {
  it('shows all four fixed playlists and at most eight personal playlists', async () => {
    const playlists = signal(Array.from({ length: 10 }, (_, index) => ({
      id: index + 1, name: `List ${index + 1}`, tracks: [], unavailableTrackCount: 0,
    })));
    TestBed.configureTestingModule({
      imports: [HomePage],
      providers: [
        provideRouter([]),
        { provide: LibraryStore, useValue: { collectionView: signal('albums') } },
        { provide: PlayerService, useValue: {} },
        { provide: PlaylistService, useValue: { playlists, load: async () => {} } },
        { provide: PlaybackSyncApi, useValue: { statistics: async () => ({ recentlyPlayed: [], continueListening: [] }) } },
        { provide: LibraryOperationTracker, useValue: { operation: signal(null) } },
        { provide: MusicLibraryApi, useValue: {
          listRecentAlbums: async () => [], listAlbumPage: async () => ({ items: [] }),
          listArtistPage: async () => ({ items: [] }), listTrackPage: async () => ({ items: [] }),
        } },
      ],
    });
    const fixture = TestBed.createComponent(HomePage);
    fixture.detectChanges();
    await fixture.whenStable();
    const section = fixture.nativeElement.querySelector('[aria-labelledby="home-playlists-heading"]') as HTMLElement;
    expect(section.querySelectorAll('.home-playlist')).toHaveLength(12);
    expect(section.textContent).toContain('Album radio');
    expect(section.textContent).toContain('Track radio');
    expect(section.textContent).toContain('List 8');
    expect(section.textContent).not.toContain('List 9');
  });

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
        { provide: PlaylistService, useValue: { playlists: signal([]), load: async () => {} } },
        { provide: PlaybackSyncApi, useValue: { statistics: async () => ({ recentlyPlayed: [], continueListening: [] }) } },
        { provide: LibraryOperationTracker, useValue: { operation } },
        {
          provide: MusicLibraryApi,
          useValue: {
            listRecentAlbums: async () => { recentCalls++; return albums; },
            listAlbumPage: async () => ({ items: [] }),
            listArtistPage: async () => ({ items: [] }),
            listTrackPage: async () => ({ items: [] }),
            mapTracks: (tracks: unknown[]) => tracks,
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

  it('shows play counts and resumes a partial track at its saved position', async () => {
    const track = {
      id: 1, title: 'Song', artist: 'Artist', album: 'Album', durationSeconds: 100, coverUrl: '',
    };
    const item = { track, positionSeconds: 25, playCount: 2, lastPlayedAt: '' };
    const completed = { ...item, positionSeconds: 99 };
    const play = vi.fn();
    TestBed.configureTestingModule({
      imports: [HomePage],
      providers: [
        provideRouter([]),
        { provide: LibraryStore, useValue: { collectionView: signal('albums') } },
        { provide: PlayerService, useValue: { play } },
        { provide: PlaylistService, useValue: { playlists: signal([]), load: async () => {} } },
        { provide: LibraryOperationTracker, useValue: { operation: signal(null) } },
        { provide: PlaybackSyncApi, useValue: { statistics: async () => ({ recentlyPlayed: [completed], continueListening: [item] }) } },
        { provide: MusicLibraryApi, useValue: {
          listRecentAlbums: async () => [],
          listAlbumPage: async () => ({ items: [] }),
          listArtistPage: async () => ({ items: [] }),
          listTrackPage: async () => ({ items: [] }),
          mapTracks: (tracks: unknown[]) => tracks,
        } },
      ],
    });
    const fixture = TestBed.createComponent(HomePage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const section = fixture.nativeElement.querySelector('[aria-labelledby="home-continue-heading"]') as HTMLElement;
    expect(section.textContent).toContain('2 plays');
    expect(section.querySelector('[role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('25');
    const recent = fixture.nativeElement.querySelector('[aria-labelledby="home-played-heading"]') as HTMLElement;
    expect(recent.querySelector('[role="progressbar"]')).toBeNull();
    section.querySelector('button')!.click();
    expect(play).toHaveBeenCalledWith([track], 0, 25);
  });
});
