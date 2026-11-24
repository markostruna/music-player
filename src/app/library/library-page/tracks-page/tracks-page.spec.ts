import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { LibraryStore } from '@shared/services/library-store';
import { PlayerService } from '@shared/services/player.service';
import { PlaylistService } from '@shared/services/playlist.service';
import { TracksPage } from './tracks-page';

describe('TracksPage', () => {
  it('uses the shared track grid and loads playlists only when active', async () => {
    const track = { id: 1, title: 'Song', artist: 'Artist', album: 'Album', coverUrl: '' };
    const loadTracks = vi.fn().mockResolvedValue(undefined);
    const loadPlaylists = vi.fn().mockResolvedValue(undefined);
    const play = vi.fn();
    TestBed.configureTestingModule({
      imports: [TracksPage],
      providers: [
        {
          provide: LibraryStore,
          useValue: {
            searchQuery: signal(''),
            pageTracks: signal([track]),
            totalTrackCount: signal(1),
            pageLoading: signal({ tracks: false }),
            pageErrors: signal({ tracks: '' }),
            currentPages: signal({ tracks: 1 }),
            loadTrackPage: loadTracks,
          },
        },
        { provide: PlayerService, useValue: { play, currentTrack: signal(track), isPlaying: signal(true) } },
        { provide: PlaylistService, useValue: { playlists: signal([]), load: loadPlaylists } },
      ],
    });

    const fixture = TestBed.createComponent(TracksPage);
    fixture.detectChanges();
    expect(loadPlaylists).not.toHaveBeenCalled();
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(loadTracks).toHaveBeenCalledOnce();
    expect(loadPlaylists).toHaveBeenCalledOnce();
    expect(fixture.nativeElement.querySelector('.track-tile.now-playing')).not.toBeNull();
    (fixture.nativeElement.querySelector('.track-tile-button') as HTMLButtonElement).click();
    expect(play).toHaveBeenCalledWith([track], 0);
  });
});
