import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { MusicLibraryApi } from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { PlaylistService } from '@shared/services/playlist.service';
import { SearchPage } from './search-page';

describe('SearchPage', () => {
  it('uses library track tiles to play and add search results to playlists', async () => {
    const params = new BehaviorSubject(convertToParamMap({ genre: 'rOcK', status: 'unplayed' }));
    const track = { id: 1, title: 'Song', artist: 'Artist', album: 'Album', coverUrl: '' };
    const browse = vi.fn().mockResolvedValue({ items: [track], totalCount: 1, page: 1, pageSize: 50 });
    const play = vi.fn();
    const playlist = { id: 5, name: 'Favorites', tracks: [], unavailableTrackCount: 0, backgroundId: 1 };
    const load = vi.fn().mockResolvedValue(undefined);
    const get = vi.fn().mockResolvedValue(playlist);
    const update = vi.fn().mockResolvedValue(undefined);
    const genres = [
      { name: 'Rock', fromTags: true, fromAnalysis: true },
      { name: 'Jazz', fromTags: true, fromAnalysis: false },
      { name: 'Pop', fromTags: false, fromAnalysis: true },
    ];
    TestBed.configureTestingModule({
      imports: [SearchPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: params } },
        { provide: MusicLibraryApi, useValue: { browseListeningTracks: browse, listGenreSources: vi.fn().mockResolvedValue(genres) } },
        { provide: PlayerService, useValue: { play, currentTrack: signal(track), isPlaying: signal(true) } },
        { provide: PlaylistService, useValue: { playlists: signal([playlist]), load, get, update } },
      ],
    });

    const fixture = TestBed.createComponent(SearchPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(browse).toHaveBeenCalledWith('', 'rOcK', 'unplayed', 1, 50);
    const genreSelect = fixture.nativeElement.querySelector('select[name="genre"]') as HTMLSelectElement;
    expect(genreSelect.value).toBe('Rock');
    expect(genreSelect.classList.contains('genre-source--both')).toBe(true);
    expect(genreSelect.querySelector('option[value="Jazz"]')?.classList.contains('genre-source--tags')).toBe(true);
    expect(genreSelect.querySelector('option[value="Pop"]')?.textContent).toContain('Analyzed');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    genreSelect.value = 'Pop';
    genreSelect.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
      queryParams: { query: null, genre: 'Pop', status: 'unplayed' },
    }));
    expect(fixture.nativeElement.textContent).toContain('Song');
    expect(fixture.nativeElement.textContent).not.toContain('No tracks match these filters.');
    expect(load).toHaveBeenCalledOnce();
    expect(fixture.nativeElement.querySelector('.track-tile.now-playing')).not.toBeNull();
    (fixture.nativeElement.querySelector('.track-tile-button') as HTMLButtonElement).click();
    expect(play).toHaveBeenCalledWith([track], 0);

    const select = fixture.nativeElement.querySelector('select[aria-label="Add Song to playlist"]') as HTMLSelectElement;
    select.value = '5';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(get).toHaveBeenCalledWith(5);
    expect(update).toHaveBeenCalledWith({ ...playlist, tracks: [track] });

    params.next(convertToParamMap({ status: 'progress' }));
    await fixture.whenStable();
    expect(browse).toHaveBeenLastCalledWith('', '', 'progress', 1, 50);
  });

  it('shows the empty-search message when no tracks match', async () => {
    TestBed.configureTestingModule({
      imports: [SearchPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: new BehaviorSubject(convertToParamMap({})) } },
        { provide: MusicLibraryApi, useValue: {
          browseListeningTracks: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 50 }),
          listGenreSources: vi.fn().mockResolvedValue([]),
        } },
        { provide: PlayerService, useValue: { play: vi.fn(), currentTrack: signal(null), isPlaying: signal(false) } },
        { provide: PlaylistService, useValue: { playlists: signal([]), load: vi.fn().mockResolvedValue(undefined) } },
      ],
    });

    const fixture = TestBed.createComponent(SearchPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No tracks match these filters.');
    expect(fixture.nativeElement.querySelector('.track-tile')).toBeNull();
  });

  it('preserves a URL genre and reports errors if the genre list cannot load', async () => {
    TestBed.configureTestingModule({
      imports: [SearchPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: new BehaviorSubject(convertToParamMap({ genre: 'Rare genre' })) } },
        { provide: MusicLibraryApi, useValue: {
          browseListeningTracks: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 50 }),
          listGenreSources: vi.fn().mockRejectedValue(new Error('Offline')),
        } },
        { provide: PlayerService, useValue: { play: vi.fn(), currentTrack: signal(null), isPlaying: signal(false) } },
        { provide: PlaylistService, useValue: { playlists: signal([]), load: vi.fn().mockResolvedValue(undefined) } },
      ],
    });

    const fixture = TestBed.createComponent(SearchPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('select[name="genre"]').value).toBe('Rare genre');
    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent).toContain('Genres could not be loaded');
  });
});
