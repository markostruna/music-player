import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { MUSIC_API_BASE } from '@shared/utils/api-path';
import { MusicLibraryApi, MusicTrack } from '@shared/services/music-library-api';
import { demoInterceptor } from './demo.interceptor';

describe('demo API', () => {
  let http: HttpClient;
  let network: HttpTestingController;

  beforeEach(() => {
    class DemoURL extends URL {
      static override createObjectURL = vi.fn(() => 'blob:demo-audio');
    }
    vi.stubGlobal('URL', DemoURL);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([demoInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    network = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    network.verify();
    vi.unstubAllGlobals();
  });

  it('serves a populated library with local artwork and generated audio', async () => {
    const tracks = await firstValueFrom(
      http.get<(MusicTrack & { artistImageUrl: string })[]>(`${MUSIC_API_BASE}/library/tracks`),
    );
    expect(tracks).toHaveLength(240);
    expect(new Set(tracks.map((track) => track.album)).size).toBe(30);
    expect(new Set(tracks.map((track) => track.artist)).size).toBe(20);
    expect(new Set(tracks.map((track) => track.albumCoverUrl)).size).toBe(30);
    expect(new Set(tracks.map((track) => track.artistImageUrl)).size).toBe(20);
    expect(tracks.every((track) => track.coverUrl !== track.artistImageUrl)).toBe(true);
    for (const artist of new Set(tracks.map((track) => track.artist))) {
      expect(
        new Set(
          tracks.filter((track) => track.artist === artist).map((track) => track.artistImageUrl),
        ).size,
      ).toBe(1);
    }
    expect(new Set(tracks.map((track) => track.id)).size).toBe(240);
    expect(
      [...new Set(tracks.map((track) => track.album))].map(
        (album) => tracks.filter((track) => track.album === album).length,
      ),
    ).toEqual(Array.from({ length: 30 }, () => 8));
    const roots = await firstValueFrom(
      http.get<{ trackCount: number }[]>(`${MUSIC_API_BASE}/admin/roots`),
    );
    expect(roots[0].trackCount).toBe(240);
    expect(tracks[0].coverUrl).toContain('/demo/first-light.png');
    expect(tracks[0].streamUrl).toBe('blob:demo-audio');
    expect(URL.createObjectURL).toHaveBeenCalledTimes(30);
  });

  it('supports sign-out, sign-in, and theme changes without network requests', async () => {
    await firstValueFrom(http.post(`${MUSIC_API_BASE}/auth/logout`, {}));
    await expect(firstValueFrom(http.get(`${MUSIC_API_BASE}/auth/me`))).rejects.toMatchObject({
      status: 401,
    });
    await firstValueFrom(
      http.post(`${MUSIC_API_BASE}/auth/login`, { email: 'any@example.com', password: 'anything' }),
    );
    const user = await firstValueFrom(
      http.put<{ theme: string }>(`${MUSIC_API_BASE}/auth/theme`, { theme: 'Blue' }),
    );
    expect(user.theme).toBe('Blue');
  });

  it('keeps edits and catalog suggestions consistent in memory', async () => {
    await firstValueFrom(
      http.post(`${MUSIC_API_BASE}/admin/tracks/metadata`, {
        trackIds: [1],
        changes: { title: 'Edited track' },
      }),
    );
    const tracks = await firstValueFrom(http.get<MusicTrack[]>(`${MUSIC_API_BASE}/library/tracks`));
    expect(tracks[0].title).toBe('Edited track');
    const suggestions = await firstValueFrom(
      http.post<{ matches: { id: string; title: string }[] }>(
        `${MUSIC_API_BASE}/admin/identify/suggestions`,
        { trackIds: [1], provider: 'demo' },
      ),
    );
    expect(suggestions.matches[0]).toMatchObject({ id: '1', title: 'First Light' });
    const detail = await firstValueFrom(
      http.get<{ tracks: { title: string }[] }>(`${MUSIC_API_BASE}/admin/identify/matches/demo/1`),
    );
    expect(detail.tracks[0].title).toBe('Edited track');
  });

  it('simulates scans and keeps uploaded artwork in memory', async () => {
    const operation = await firstValueFrom(
      http.post<{ id: string; state: string }>(`${MUSIC_API_BASE}/admin/roots/1/scan`, {}),
    );
    expect(operation.state).toBe('running');
    const result = await firstValueFrom(
      http.get<{ state: string }>(`${MUSIC_API_BASE}/admin/operations/${operation.id}`),
    );
    expect(result.state).toBe('completed');

    const svg = btoa('<svg xmlns="http://www.w3.org/2000/svg"></svg>');
    await firstValueFrom(
      http.post(`${MUSIC_API_BASE}/admin/tracks/cover`, { trackIds: [1], image: svg }),
    );
    const tracks = await firstValueFrom(http.get<MusicTrack[]>(`${MUSIC_API_BASE}/library/tracks`));
    expect(tracks[0].coverUrl).toBe(`data:image/svg+xml;base64,${svg}`);
  });

  it('supports the current paginated library, details, and folder APIs', async () => {
    const api = TestBed.inject(MusicLibraryApi);
    const albums = await api.listAlbumPage('aurora', 1, 1);
    expect(albums.totalCount).toBe(2);
    expect(albums.items).toHaveLength(1);
    const artists = await api.listArtistPage('', 1, 10);
    expect(artists.totalCount).toBe(20);
    expect(artists.items.find((artist) => artist.artist === 'Aurora Drive')?.albumCount).toBe(2);
    const tracks = await api.listAlbumTracksByTrackId(1);
    expect(tracks).toHaveLength(8);
    expect(tracks[0].tileCoverUrl).toContain('first-light.png');
    expect(await api.getArtistMetadata('Aurora Drive')).toMatchObject({
      imageMissing: false,
      description: expect.stringContaining('fictional'),
    });
    expect((await api.browseLibraryFolder(1, 'Aurora Drive')).folders).toHaveLength(2);
    expect(
      await api.searchTracks('', { sourceRootId: 1, path: 'Aurora Drive/First Light' }),
    ).toHaveLength(8);
  });

  it('fails unsupported API calls instead of falling through to the network', async () => {
    await expect(firstValueFrom(http.get(`${MUSIC_API_BASE}/unknown`))).rejects.toBeInstanceOf(
      HttpErrorResponse,
    );
    await expect(
      firstValueFrom(http.post(`${MUSIC_API_BASE}/admin/metadata/albums/refresh`, {})),
    ).rejects.toMatchObject({ status: 501 });
  });
});
