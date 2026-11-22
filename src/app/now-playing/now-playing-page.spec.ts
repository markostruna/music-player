import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { MusicLibraryApi } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { NowPlayingPage } from './now-playing-page';

describe('NowPlayingPage queue artwork', () => {
  it('shows each track cover between its number and details, with a fallback', async () => {
    const first = {
      title: 'First', artist: 'Artist', album: 'Album', albumArtist: 'Artist',
      albumCoverUrl: '/album', coverUrl: '/first-cover', durationSeconds: 120, year: 2020,
    };
    const second = { ...first, title: 'Second', coverUrl: '/second-cover' };
    const queue = signal([first, second]);
    const playFromQueue = vi.fn();
    TestBed.configureTestingModule({
      imports: [NowPlayingPage],
      providers: [
        provideRouter([]),
        { provide: PlayerService, useValue: {
          currentTrack: signal(first), queue, currentIndex: signal(-1), isPlaying: signal(false),
          syncEnabled: signal(false), radioMode: signal('off'), playFromQueue,
        } },
        { provide: MusicLibraryApi, useValue: {
          getArtistMetadata: async () => ({ imageUrl: null }),
        } },
      ],
    });

    const fixture = TestBed.createComponent(NowPlayingPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const rows = fixture.nativeElement.querySelectorAll('.now-playing-queue-row') as NodeListOf<HTMLButtonElement>;
    expect(rows).toHaveLength(2);
    expect(Array.from(rows[0].children).slice(0, 3).map((child) => child.className)).toEqual([
      'album-track-number', 'now-playing-queue-art', 'album-track-title',
    ]);
    const firstImage = rows[0].querySelector('img')!;
    expect(firstImage.getAttribute('src')).toBe('/first-cover');
    expect(rows[1].querySelector('img')!.getAttribute('src')).toBe('/second-cover');
    firstImage.dispatchEvent(new Event('error'));
    expect(firstImage.style.visibility).toBe('hidden');
    expect(rows[0].querySelector('.now-playing-queue-art')!.textContent).toContain('♫');
    firstImage.dispatchEvent(new Event('load'));
    expect(firstImage.style.visibility).toBe('visible');
    rows[1].click();
    expect(playFromQueue).toHaveBeenCalledWith(1);
  });
});
