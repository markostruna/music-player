import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { PlayerService } from '@shared/services/player.service';
import { Playlist, PlaylistService } from '@shared/services/playlist.service';
import { PlaylistDetail } from './playlist-detail';

describe('PlaylistDetail backgrounds', () => {
  it('saves a selected background without changing the playlist name or tracks', async () => {
    const playlist: Playlist = {
      id: 1, name: 'Mix', tracks: [], unavailableTrackCount: 0, backgroundId: 1,
    };
    const update = vi.fn(async (value: Playlist) => value);
    TestBed.configureTestingModule({
      imports: [PlaylistDetail],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: '1' }) } } },
        { provide: PlaylistService, useValue: { get: async () => playlist, update } },
        { provide: PlayerService, useValue: {} },
      ],
    });
    const fixture = TestBed.createComponent(PlaylistDetail);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const buttons = fixture.nativeElement.querySelectorAll('.playlist-background-option') as NodeListOf<HTMLButtonElement>;
    expect(buttons.length).toBe(20);
    expect(buttons[0].getAttribute('aria-pressed')).toBe('true');
    buttons[19].click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(update).toHaveBeenCalledWith({ ...playlist, backgroundId: 20 });
    expect(buttons[19].getAttribute('aria-pressed')).toBe('true');
    expect(fixture.nativeElement.querySelector('.playlist-detail-art').getAttribute('src'))
      .toContain('background-20.webp');
  });

  it('shows fixed artwork without a background picker for built-in playlists', () => {
    TestBed.configureTestingModule({
      imports: [PlaylistDetail],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'album-radio' }) } } },
        { provide: PlaylistService, useValue: {} },
        { provide: PlayerService, useValue: {} },
      ],
    });
    const fixture = TestBed.createComponent(PlaylistDetail);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.playlist-detail-art').getAttribute('src'))
      .toContain('album-radio.webp');
    expect(fixture.nativeElement.querySelector('.playlist-background-picker')).toBeNull();
  });
});
