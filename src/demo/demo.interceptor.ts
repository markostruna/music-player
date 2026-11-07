import { DOCUMENT } from '@angular/common';
import {
  HttpErrorResponse,
  HttpInterceptorFn,
  HttpRequest,
  HttpResponse,
} from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { of, throwError } from 'rxjs';
import { AlbumMatch } from '@shared/services/album-identify-api';
import { ThemeName, UserRole } from '@shared/services/auth-state';
import {
  LibraryOperationStatus,
  ManagedUser,
  MetadataPatch,
  AlbumSummary,
  ArtistSummary,
  MusicFolder,
  MusicTrack,
  SourceRoot,
} from '@shared/services/music-library-api';
import { MUSIC_API_BASE } from '@shared/utils/api-path';

interface DemoBody {
  email: string;
  role: UserRole;
  theme: ThemeName;
  name: string;
  path: string;
  isEnabled: boolean;
  trackIds: number[];
  changes: MetadataPatch;
  image: string;
  artist: string;
  album: string;
  description: string;
  destinationFolderId: number;
  provider: string;
  year: number | null;
  destinationRootId: number;
  providerId: string;
}

interface DemoTrack extends MusicTrack {
  artistDescription: string;
  artistImageUrl: string;
  artistImageMissing: boolean;
}

const DEMO_ALBUMS = [
  {
    artist: 'Aurora Drive',
    title: 'First Light',
    year: 2022,
    genre: 'Indie electronic',
    image: 'first-light.png',
    artistImage: 'aurora-drive-artist.png',
    songs: [
      'Daybreak',
      'Open Roads',
      'Soft Focus',
      'A Place to Begin',
      'Amber Skies',
      'Slow Motion',
      'Morning Lines',
      'New Horizon',
    ],
  },
  {
    artist: 'Aurora Drive',
    title: 'Golden Hour',
    year: 2024,
    genre: 'Indie electronic',
    image: 'golden-hour.png',
    artistImage: 'aurora-drive-artist.png',
    songs: [
      'Sunroom',
      'Golden Hour',
      'Wildflower',
      'The Long Way Home',
      'Warm Light',
      'Summer Windows',
      'Stay a Little',
      'Last Train Home',
    ],
  },
  {
    artist: 'The Paper Sea',
    title: 'Slow Tides',
    year: 2023,
    genre: 'Ambient',
    image: 'slow-tides.png',
    artistImage: 'paper-sea-artist.png',
    songs: [
      'Blue Water',
      'Driftwood',
      'Between Waves',
      'A Quiet Shore',
      'Shoreline',
      'Low Tide',
      'Glass Harbor',
      'Back to Blue',
    ],
  },
  {
    artist: 'Luna North',
    title: 'Night Signals',
    year: 2025,
    genre: 'Downtempo',
    image: 'night-signals.png',
    artistImage: 'luna-north-artist.png',
    songs: [
      'Night Signals',
      'Satellite',
      'After Midnight',
      'Lights Below',
      'Silver Lines',
      'Quiet Orbit',
      'Far Side',
      'Before Dawn',
    ],
  },
  {
    artist: 'The Paper Sea',
    title: 'Saltwater Letters',
    year: 2021,
    genre: 'Ambient',
    image: 'saltwater-letters.png',
    artistImage: 'paper-sea-artist.png',
    songs: [
      'Postcard from the Coast',
      'Saltwater Letters',
      'Sea Glass',
      'Low Clouds',
      'Empty Pier',
      'Weathered',
      'A Soft Goodbye',
      'Homeward',
    ],
  },
  {
    artist: 'Luna North',
    title: 'Blue Hours',
    year: 2023,
    genre: 'Downtempo',
    image: 'blue-hours.png',
    artistImage: 'luna-north-artist.png',
    songs: [
      'Blue Hours',
      'Window Lights',
      'Slow Orbit',
      'Nocturne',
      'City in the Rain',
      'Almost Morning',
      'Northbound',
      'Sleepwalking',
    ],
  },
  {
    artist: 'Velvet Pines',
    title: 'Paper Lanterns',
    year: 2024,
    genre: 'Dream pop',
    image: 'paper-lanterns.png',
    artistImage: 'velvet-pines-artist.png',
    songs: [
      'Paper Lanterns',
      'Moss Garden',
      'Feverfew',
      'Wild Country',
      'Softest Sound',
      'Half Awake',
      'Greenhouse',
      'Somewhere Near',
    ],
  },
  {
    artist: 'Static Bloom',
    title: 'Soft Machines',
    year: 2022,
    genre: 'Indie electronic',
    image: 'soft-machines.png',
    artistImage: 'static-bloom-artist.png',
    songs: [
      'Soft Machines',
      'Electric Garden',
      'Little Sparks',
      'New Patterns',
      'Familiar Shapes',
      'Signal Fade',
      'Open Circuit',
      'Afterimage',
    ],
  },
  {
    artist: 'Harborlight',
    title: 'Coastlines',
    year: 2020,
    genre: 'Indie folk',
    image: 'coastlines.png',
    artistImage: 'harborlight-artist.png',
    songs: [
      'Coastlines',
      'Old Maps',
      'Harborlight',
      'North Pier',
      'Tide Marks',
      'Clear Water',
      'Far from Here',
      'Safe Return',
    ],
  },
  {
    artist: 'Morrow Lines',
    title: 'Small Hours',
    year: 2025,
    genre: 'Downtempo',
    image: 'small-hours.png',
    artistImage: 'morrow-lines-artist.png',
    songs: [
      'Small Hours',
      'Last Light',
      'Quiet Company',
      'Distant Rooms',
      'Halfway Home',
      'Winter Window',
      'Stay Until Morning',
      'The Other Side',
    ],
  },
  {
    artist: 'Cedar Arcade',
    title: 'Neon Season',
    year: 2022,
    genre: 'Indie electronic',
    image: 'neon-season.png',
    artistImage: 'cedar-arcade-artist.png',
    songs: [
      'Afterimage City',
      'Glass Elevator',
      'Summer Current',
      'Late Bus',
      'Neon Season',
      'Midnight Market',
      'Blue Receipt',
      "Tomorrow's Light",
    ],
  },
  {
    artist: 'Cedar Arcade',
    title: 'Side Streets',
    year: 2024,
    genre: 'Indie electronic',
    image: 'side-streets.png',
    artistImage: 'cedar-arcade-artist.png',
    songs: [
      'Side Streets',
      'Corner Store',
      'Empty Map',
      'Open Late',
      'Rain on Eighth',
      'Passing Window',
      'Small Detour',
      'Back Again',
    ],
  },
  {
    artist: 'Iris Static',
    title: 'Small Fires',
    year: 2021,
    genre: 'Dream pop',
    image: 'small-fires.png',
    artistImage: 'iris-static-artist.png',
    songs: [
      'Kindling',
      'Small Fires',
      'Slow Burn',
      'Night Bloom',
      'Red Thread',
      'Ember Room',
      'Gather Round',
      'Soft Ash',
    ],
  },
  {
    artist: 'Iris Static',
    title: 'Violet Current',
    year: 2024,
    genre: 'Dream pop',
    image: 'violet-current.png',
    artistImage: 'iris-static-artist.png',
    songs: [
      'Violet Current',
      'Pull of Water',
      'Petal Static',
      'Deep End',
      'Color Drift',
      'Undertow',
      'Moon Pool',
      'Still Bright',
    ],
  },
  {
    artist: 'Echo Meridian',
    title: 'Faraway Rooms',
    year: 2023,
    genre: 'Ambient',
    image: 'faraway-rooms.png',
    artistImage: 'echo-meridian-artist.png',
    songs: [
      'Faraway Rooms',
      'Paper Walls',
      'Guest Light',
      'Long Distance',
      'Another Hall',
      'Open Door',
      'Lost Keys',
      'Stay Here',
    ],
  },
  {
    artist: 'Echo Meridian',
    title: 'Parallel Lines',
    year: 2025,
    genre: 'Ambient',
    image: 'parallel-lines.png',
    artistImage: 'echo-meridian-artist.png',
    songs: [
      'Parallel Lines',
      'Crossfade',
      'Two Horizons',
      'Familiar Stranger',
      'Meeting Point',
      'Nearly There',
      'Signal to Noise',
      'Separate Ways',
    ],
  },
  {
    artist: 'Marigold Motel',
    title: 'Vacancy Signs',
    year: 2021,
    genre: 'Indie pop',
    image: 'vacancy-signs.png',
    artistImage: 'marigold-motel-artist.png',
    songs: [
      'Vacancy Signs',
      'Room Twelve',
      'Swimming Pool',
      'After Hours',
      'Keep the Change',
      'Highway Flowers',
      'Rest Stop',
      'Leave a Light',
    ],
  },
  {
    artist: 'Marigold Motel',
    title: 'Late Checkout',
    year: 2024,
    genre: 'Indie pop',
    image: 'late-checkout.png',
    artistImage: 'marigold-motel-artist.png',
    songs: [
      'Late Checkout',
      'Curtain Call',
      'Hotel Coffee',
      'Morning Bell',
      'One More Night',
      'Folded Note',
      'Turn the Key',
      'Downstairs',
    ],
  },
  {
    artist: 'Northern Vale',
    title: 'Under Snow',
    year: 2022,
    genre: 'Indie folk',
    image: 'under-snow.png',
    artistImage: 'northern-vale-artist.png',
    songs: [
      'Under Snow',
      'Pines in White',
      'Longest Night',
      'Warm Cabin',
      'Frozen River',
      'Snowblind',
      'Hearth Song',
      'Thaw',
    ],
  },
  {
    artist: 'Northern Vale',
    title: 'Quiet Valley',
    year: 2025,
    genre: 'Indie folk',
    image: 'quiet-valley.png',
    artistImage: 'northern-vale-artist.png',
    songs: [
      'Quiet Valley',
      'Clear Morning',
      'Hillside',
      'Slow Stream',
      'Valley Floor',
      'Birdsong',
      'Open Sky',
      'Rest Easy',
    ],
  },
  {
    artist: 'Chrome Orchard',
    title: 'Glass Engines',
    year: 2023,
    genre: 'Indie electronic',
    image: 'glass-engines.png',
    artistImage: 'chrome-orchard-artist.png',
    songs: [
      'Glass Engines',
      'Silver Wire',
      'Machines Dream',
      'Orchard at Night',
      'Loose Fitting',
      'Copper Sound',
      'Take Shape',
      'Polished Stone',
    ],
  },
  {
    artist: 'Chrome Orchard',
    title: 'Silver Fruit',
    year: 2025,
    genre: 'Indie electronic',
    image: 'silver-fruit.png',
    artistImage: 'chrome-orchard-artist.png',
    songs: [
      'Silver Fruit',
      'Bitter Sweet',
      'Golden Seeds',
      'Skin and Light',
      'Orchard Moon',
      'A Little More',
      'New Weather',
      'The Whole Thing',
    ],
  },
  {
    artist: 'Waking Atlas',
    title: 'The Long Elsewhere',
    year: 2020,
    genre: 'Alternative',
    image: 'long-elsewhere.png',
    artistImage: 'waking-atlas-artist.png',
    songs: [
      'The Long Elsewhere',
      'Map of Nothing',
      'Compass Rose',
      'Wayside',
      'Far Country',
      'Wandering',
      'Once Around',
      'Here We Are',
    ],
  },
  {
    artist: 'Waking Atlas',
    title: 'Open Country',
    year: 2024,
    genre: 'Alternative',
    image: 'open-country.png',
    artistImage: 'waking-atlas-artist.png',
    songs: [
      'Open Country',
      'Shallow River',
      'Wind Farm',
      'High Summer',
      'Wildflowers',
      'No Fences',
      'Southern Road',
      'The View',
    ],
  },
  {
    artist: 'Glass House Choir',
    title: 'Rooms of Air',
    year: 2023,
    genre: 'Ambient',
    image: 'rooms-of-air.png',
    artistImage: 'glass-house-choir-artist.png',
    songs: [
      'Rooms of Air',
      'Whispered Floor',
      'Transparent',
      'Second Voice',
      'Blue Chamber',
      'Choir Practice',
      'Window Song',
      'Stillness',
    ],
  },
  {
    artist: 'Blue Paper',
    title: 'Margins',
    year: 2022,
    genre: 'Indie folk',
    image: 'margins.png',
    artistImage: 'blue-paper-artist.png',
    songs: [
      'Margins',
      'Blue Paper',
      'Footnote',
      'Untitled Page',
      'Soft Pencil',
      'Not Quite',
      'Folded Corner',
      'Blank Space',
    ],
  },
  {
    artist: 'Cinderlake',
    title: 'Ash and Water',
    year: 2025,
    genre: 'Alternative',
    image: 'ash-and-water.png',
    artistImage: 'cinderlake-artist.png',
    songs: [
      'Ash and Water',
      'Basalt',
      'Lake Effect',
      'Smoke Trail',
      'Dark Current',
      'Cinder Shore',
      'Before Rain',
      'Beneath the Surface',
    ],
  },
  {
    artist: 'Low Season',
    title: 'Off Season',
    year: 2021,
    genre: 'Dream pop',
    image: 'off-season.png',
    artistImage: 'low-season-artist.png',
    songs: [
      'Off Season',
      'Empty Boardwalk',
      'Blue Umbrella',
      'Closed Signs',
      'No Reservations',
      'Early Tide',
      'Windbreak',
      'See You Soon',
    ],
  },
  {
    artist: 'The Soft Array',
    title: 'Color Theory',
    year: 2024,
    genre: 'Indie electronic',
    image: 'color-theory.png',
    artistImage: 'soft-array-artist.png',
    songs: [
      'Color Theory',
      'Primary',
      'Muted',
      'Gradient',
      'Color Study',
      'Saturation',
      'Hue Shift',
      'White Space',
    ],
  },
  {
    artist: 'Pine Satellite',
    title: 'Almost There',
    year: 2025,
    genre: 'Downtempo',
    image: 'almost-there.png',
    artistImage: 'pine-satellite-artist.png',
    songs: [
      'Almost There',
      'Signal Fires',
      'Pine Satellite',
      'Another Night',
      'Coordinate',
      'Small World',
      'Receiver',
      'Arrival',
    ],
  },
] as const;

const DEMO_TRACK_COUNT = DEMO_ALBUMS.reduce((total, album) => total + album.songs.length, 0);

@Injectable({ providedIn: 'root' })
export class DemoBackend {
  private readonly document = inject(DOCUMENT);
  private readonly now = new Date().toISOString();
  private readonly audioUrls = new Map<number, string>();
  private signedIn = true;
  private user = {
    id: 1,
    email: 'demo@example.com',
    role: 'Admin' as UserRole,
    theme: 'Dark' as ThemeName,
  };
  private users: ManagedUser[] = [
    { id: 1, email: 'demo@example.com', role: 'Admin', isEnabled: true, createdAt: this.now },
    { id: 2, email: 'guest@example.com', role: 'Guest', isEnabled: true, createdAt: this.now },
  ];
  private roots: SourceRoot[] = [
    {
      id: 1,
      name: 'Demo collection',
      path: 'Demo Music',
      trackCount: DEMO_TRACK_COUNT,
      isEnabled: true,
    },
  ];
  private folders: MusicFolder[] = DEMO_ALBUMS.map((album, index) => ({
    id: index + 1,
    sourceRootId: 1,
    name: album.title,
    relativePath: `${album.artist}/${album.title}`,
  }));
  private tracks = this.createTracks();
  private operation: LibraryOperationStatus | null = null;
  private defaultProvider = 'demo';

  handle(request: HttpRequest<unknown>): unknown {
    const url = new URL(request.urlWithParams, this.document.baseURI);
    const path = url.pathname.slice(MUSIC_API_BASE.length);
    const method = request.method;
    const body = request.body as DemoBody;

    if (path === '/auth/csrf' && method === 'GET') return { token: 'demo-token' };
    if (path === '/auth/login' && method === 'POST') {
      this.signedIn = true;
      return this.user;
    }
    if (path === '/auth/me' && method === 'GET') {
      if (!this.signedIn) throw this.error(401, 'Sign in to the demo.');
      return this.user;
    }
    if (!this.signedIn) throw this.error(401, 'Sign in to the demo.');
    if (path === '/auth/logout' && method === 'POST') {
      this.signedIn = false;
      return null;
    }
    if (path === '/auth/refresh' && method === 'POST') return null;
    if (path === '/auth/theme' && method === 'PUT') {
      this.user = { ...this.user, theme: body.theme };
      return this.user;
    }
    if (path === '/library/tracks' && method === 'GET') return this.tracks;
    if (path === '/library/tracks/search' && method === 'GET') return this.filteredTracks(url);
    if (path === '/library/tracks/page' && method === 'GET') {
      return this.paginate(this.filteredTracks(url), url);
    }
    if (path === '/library/albums/page' && method === 'GET') {
      const albums = new Map<string, AlbumSummary>();
      for (const track of this.tracks) {
        const key = `${track.albumArtist}\0${track.album}`;
        const album = albums.get(key);
        if (album) {
          album.trackCount++;
          album.durationSeconds += track.durationSeconds;
        } else {
          albums.set(key, {
            artist: track.albumArtist,
            album: track.album,
            releaseYear: track.year,
            trackCount: 1,
            durationSeconds: track.durationSeconds,
            coverTrackId: track.id,
            coverUrl: track.coverUrl,
          });
        }
      }
      const query = (url.searchParams.get('query') ?? '').toLowerCase();
      return this.paginate(
        [...albums.values()].filter((album) =>
          `${album.artist} ${album.album}`.toLowerCase().includes(query),
        ),
        url,
      );
    }
    if (path === '/library/artists/page' && method === 'GET') {
      const artists = new Map<string, ArtistSummary & { albums: Set<string> }>();
      for (const track of this.tracks) {
        let artist = artists.get(track.artist);
        if (!artist) {
          artist = {
            artist: track.artist,
            trackCount: 0,
            albumCount: 0,
            coverUrl: track.coverUrl,
            artistImageUrl: track.artistImageUrl,
            artistImageMissing: track.artistImageMissing,
            albums: new Set<string>(),
          };
          artists.set(track.artist, artist);
        }
        artist.trackCount++;
        artist.albums.add(track.album);
        artist.albumCount = artist.albums.size;
      }
      const query = (url.searchParams.get('query') ?? '').toLowerCase();
      return this.paginate(
        [...artists.values()]
          .filter((artist) => artist.artist.toLowerCase().includes(query))
          .map(({ albums, ...artist }) => artist),
        url,
      );
    }
    if (path === '/library/albums/tracks' && method === 'GET') {
      return this.tracks.filter(
        (track) =>
          track.albumArtist === url.searchParams.get('artist') &&
          track.album === url.searchParams.get('album'),
      );
    }
    const byTrack = /^\/library\/albums\/by-track\/(\d+)\/tracks$/.exec(path);
    if (byTrack && method === 'GET') {
      const track = this.tracks.find((item) => item.id === Number(byTrack[1]));
      if (!track) throw this.error(404, 'Demo album not found.');
      return this.tracks.filter(
        (item) => item.albumArtist === track.albumArtist && item.album === track.album,
      );
    }
    if (path === '/library/artists/tracks' && method === 'GET') {
      return this.tracks.filter(
        (track) =>
          track.artist === url.searchParams.get('artist') ||
          track.albumArtist === url.searchParams.get('artist'),
      );
    }
    if (path === '/library/artists/metadata' && method === 'GET') {
      const track = this.tracks.find((item) => item.artist === url.searchParams.get('artist'));
      if (!track) throw this.error(404, 'Demo artist not found.');
      return {
        description: track.artistDescription,
        imageUrl: track.artistImageUrl,
        imageMissing: track.artistImageMissing,
        referencedArtists: [],
      };
    }
    if (path === '/library/folders' && method === 'GET') {
      return this.roots.map((root) => ({
        sourceRootId: root.id,
        name: root.name,
        relativePath: '',
      }));
    }
    const folderPath = /^\/library\/folders\/(\d+)$/.exec(path);
    if (folderPath && method === 'GET') {
      const root = this.roots.find((item) => item.id === Number(folderPath[1]));
      if (!root) throw this.error(404, 'Demo folder not found.');
      const relativePath = url.searchParams.get('path') ?? '';
      const prefix = relativePath ? `${relativePath}/` : '';
      const children = new Set<string>();
      for (const track of this.tracks.filter(
        (item) => item.sourceRootId === root.id && item.relativePath.startsWith(prefix),
      )) {
        const remainder = track.relativePath.slice(prefix.length);
        if (remainder.includes('/')) children.add(remainder.split('/')[0]);
      }
      return {
        sourceRootId: root.id,
        rootName: root.name,
        relativePath,
        folders: [...children].map((name) => ({
          sourceRootId: root.id,
          name,
          relativePath: `${prefix}${name}`,
        })),
        files: this.tracks
          .filter(
            (track) =>
              track.sourceRootId === root.id &&
              track.relativePath.startsWith(prefix) &&
              !track.relativePath.slice(prefix.length).includes('/'),
          )
          .map((track) => ({
            name: track.relativePath.slice(prefix.length),
            relativePath: track.relativePath,
          })),
      };
    }
    if (path === '/admin/providers/default-album-provider') {
      if (method === 'PUT') this.defaultProvider = body.providerId;
      if (method === 'GET' || method === 'PUT') return { providerId: this.defaultProvider };
    }
    if (path === '/admin/providers' && method === 'GET') return this.providers();
    if (/^\/admin\/providers\/[^/]+$/.test(path) && method === 'PUT') {
      throw this.error(501, 'Provider credentials are unavailable in the demo.');
    }
    if (path === '/admin/roots/folders' && method === 'GET') return this.folders;
    if (path === '/admin/roots' && method === 'GET') return this.roots;
    if (path === '/admin/roots' && method === 'POST') {
      const root: SourceRoot = {
        id: this.nextId(this.roots),
        name: body.name,
        path: body.path,
        trackCount: 0,
        isEnabled: true,
      };
      this.roots = [...this.roots, root];
      return root;
    }
    const rootAction = /^\/admin\/roots\/(\d+)(?:\/(scan|metadata-refresh))?$/.exec(path);
    if (rootAction) {
      const id = Number(rootAction[1]);
      const root = this.roots.find((item) => item.id === id);
      if (!root) throw this.error(404, 'Demo folder not found.');
      if (method === 'DELETE' && !rootAction[2]) {
        this.roots = this.roots.filter((item) => item.id !== id);
        this.folders = this.folders.filter((folder) => folder.sourceRootId !== id);
        this.tracks = this.tracks.filter((track) => track.sourceRootId !== id);
        return null;
      }
      if (method === 'POST' && rootAction[2]) {
        this.operation = {
          id: 'demo-operation',
          sourceRootId: id,
          rootName: root.name,
          operationType: rootAction[2] === 'scan' ? 'scan' : 'metadata-refresh',
          state: 'running',
          message: 'Simulating a demo operation.',
          completedUnits: 0,
          totalUnits: 1,
          startedAt: new Date().toISOString(),
          finishedAt: null,
        };
        return this.operation;
      }
    }
    if (path === '/admin/operations/active' && method === 'GET') return null;
    if (path === '/admin/operations/demo-operation' && method === 'GET' && this.operation) {
      this.operation = {
        ...this.operation,
        state: 'completed',
        message: 'Demo operation complete.',
        completedUnits: 1,
        finishedAt: new Date().toISOString(),
      };
      return this.operation;
    }
    if (path === '/admin/tracks/metadata' && method === 'POST') {
      return this.updateTracks(body.trackIds, (track) => ({ ...track, ...body.changes }));
    }
    if (path === '/admin/tracks/cover' && method === 'POST') {
      return this.updateTracks(body.trackIds, (track) => ({
        ...track,
        coverUrl: this.imageDataUrl(body.image),
      }));
    }
    if (path === '/admin/tracks/move' && method === 'POST') {
      const folder = this.folders.find((item) => item.id === body.destinationFolderId);
      if (!folder) throw this.error(404, 'Demo destination not found.');
      return this.updateTracks(body.trackIds, (track) => ({
        ...track,
        folderId: folder.id,
        sourceRootId: folder.sourceRootId,
      }));
    }
    if (
      /^\/admin\/metadata\/(artists|albums)\/(description|image)$/.test(path) &&
      method === 'PUT'
    ) {
      const album = path.includes('/albums/');
      this.tracks = this.tracks.map((track) => {
        if (
          (album ? track.albumArtist : track.artist) !== body.artist ||
          (album && track.album !== body.album)
        )
          return track;
        if (path.endsWith('/description')) {
          return album
            ? { ...track, albumDescription: body.description }
            : {
                ...track,
                artistDescription: body.description,
                albumArtistDescription: body.description,
              };
        }
        const image = this.imageDataUrl(body.image);
        return album
          ? { ...track, coverUrl: image }
          : {
              ...track,
              artistImageUrl: image,
              albumArtistImageUrl: image,
              artistImageMissing: false,
              albumArtistImageMissing: false,
            };
      });
      return null;
    }
    if (/^\/admin\/metadata\/(artists|albums)\/refresh$/.test(path) && method === 'POST') {
      throw this.error(501, 'External metadata refresh is unavailable in the demo.');
    }
    if (path === '/users' && method === 'GET') return this.users;
    if (path === '/users' && method === 'POST') {
      const user: ManagedUser = {
        id: this.nextId(this.users),
        email: body.email,
        role: body.role,
        isEnabled: true,
        createdAt: new Date().toISOString(),
      };
      this.users = [...this.users, user];
      return user;
    }
    const userAction = /^\/users\/(\d+)\/(role|enabled|password)$/.exec(path);
    if (userAction && method === 'PUT') {
      const id = Number(userAction[1]);
      if (!this.users.some((item) => item.id === id)) throw this.error(404, 'Demo user not found.');
      this.users = this.users.map((item) =>
        item.id !== id
          ? item
          : {
              ...item,
              role: userAction[2] === 'role' ? body.role : item.role,
              isEnabled: userAction[2] === 'enabled' ? body.isEnabled : item.isEnabled,
            },
      );
      return null;
    }
    if (path.startsWith('/admin/identify/')) return this.identify(path, method, body);
    throw this.error(501, `Demo API does not implement ${method} ${path}.`);
  }

  private identify(path: string, method: string, body: DemoBody): unknown {
    if (path === '/admin/identify/providers' && method === 'GET') {
      return [{ id: 'demo', name: 'Demo catalog', isConfigured: true }];
    }
    if (path === '/admin/identify/artists/search' && method === 'POST') {
      const seen = new Set<string>();
      return this.tracks
        .filter((track) => {
          if (
            seen.has(track.artist) ||
            !track.artist.toLowerCase().includes(body.artist.toLowerCase())
          )
            return false;
          seen.add(track.artist);
          return true;
        })
        .map((track) => ({
          provider: 'demo',
          id: String(track.id),
          name: track.artist,
          detail: 'Demo catalog',
          imageUrl: null,
        }));
    }
    const artistMatch = /^\/admin\/identify\/artists\/matches\/demo\/(\d+)$/.exec(path);
    if (artistMatch && method === 'GET') {
      const track = this.tracks.find((item) => item.id === Number(artistMatch[1]));
      if (!track) throw this.error(404, 'Demo artist not found.');
      return {
        match: {
          provider: 'demo',
          id: String(track.id),
          name: track.artist,
          detail: 'Demo catalog',
          imageUrl: null,
        },
        description: track.artistDescription,
        imageUrl: null,
        imageDataUrl: track.artistImageUrl,
        imageProvider: 'Demo',
      };
    }
    if (path === '/admin/identify/suggestions' && method === 'POST') {
      const track = this.tracks.find((item) => body.trackIds.includes(item.id));
      return {
        tagArtist: track?.artist ?? null,
        tagAlbum: track?.album ?? null,
        tagYear: track?.year ?? null,
        matches: track ? [this.match(track)] : [],
      };
    }
    if (path === '/admin/identify/search' && method === 'POST') {
      const seen = new Set<string>();
      return this.tracks
        .filter((track) => {
          const key = `${track.artist}\0${track.album}`;
          if (seen.has(key)) return false;
          seen.add(key);
          return (
            (!body.artist || track.artist.toLowerCase().includes(body.artist.toLowerCase())) &&
            (!body.album || track.album.toLowerCase().includes(body.album.toLowerCase())) &&
            (!body.year || track.year === body.year)
          );
        })
        .map((track) => this.match(track));
    }
    const matchPath = /^\/admin\/identify\/matches\/demo\/(\d+)$/.exec(path);
    if (matchPath && method === 'GET') {
      const track = this.tracks.find((item) => item.id === Number(matchPath[1]));
      if (!track) throw this.error(404, 'Demo album not found.');
      return {
        match: this.match(track),
        genre: track.genre,
        musicBrainzReleaseGroupId: null,
        tracks: this.tracks
          .filter((item) => item.album === track.album && item.albumArtist === track.albumArtist)
          .map((item) => ({
            discNumber: item.discNumber,
            trackNumber: item.trackNumber,
            title: item.title,
            artist: item.artist,
            durationSeconds: item.durationSeconds,
          })),
      };
    }
    if (path === '/admin/identify/covers' && method === 'POST') {
      const track = this.tracks.find((item) => body.trackIds.includes(item.id));
      return {
        candidates: track
          ? [
              {
                id: 'demo-cover',
                source: 'Demo',
                label: 'Demo artwork',
                previewUrl: track.coverUrl,
              },
            ]
          : [],
        warnings: [],
      };
    }
    if (path === '/admin/identify/move/preview' && method === 'POST') {
      return {
        canMove: false,
        reason: 'Moving files is unavailable in the demo.',
        sourcePath: 'Demo Music',
        destinationPath: 'Demo Music',
      };
    }
    if (path === '/admin/identify/move' && method === 'POST') {
      throw this.error(501, 'Moving files is unavailable in the demo.');
    }
    throw this.error(501, `Demo API does not implement ${method} ${path}.`);
  }

  private match(track: MusicTrack): AlbumMatch {
    return {
      provider: 'demo',
      id: String(track.id),
      artist: track.albumArtist,
      title: track.album,
      year: track.year,
      trackCount: this.tracks.filter(
        (item) => item.album === track.album && item.albumArtist === track.albumArtist,
      ).length,
      coverUrl: track.coverUrl,
      detail: 'Demo catalog',
    };
  }

  private providers() {
    return [
      {
        id: 'demo',
        name: 'Demo catalog',
        description: 'Fictional local demo catalog.',
        isConfigured: true,
        credentials: [],
      },
    ];
  }

  private filteredTracks(url: URL): DemoTrack[] {
    const query = (url.searchParams.get('query') ?? '').toLowerCase();
    const rootId = url.searchParams.get('sourceRootId');
    const folder = url.searchParams.get('folderPath');
    return this.tracks.filter(
      (track) =>
        `${track.title} ${track.artist} ${track.album}`.toLowerCase().includes(query) &&
        (!rootId || track.sourceRootId === Number(rootId)) &&
        (!folder || track.relativePath.startsWith(`${folder}/`)),
    );
  }

  private paginate<T>(items: T[], url: URL) {
    const page = Math.max(1, Number(url.searchParams.get('page')) || 1);
    const pageSize = Math.max(1, Number(url.searchParams.get('pageSize')) || 20);
    return {
      items: items.slice((page - 1) * pageSize, page * pageSize),
      totalCount: items.length,
      page,
      pageSize,
    };
  }

  private updateTracks(
    ids: number[],
    update: (track: DemoTrack) => DemoTrack,
  ): { results: { trackId: number; succeeded: boolean; message: string }[] } {
    const results = ids.map((id) => {
      const found = this.tracks.some((track) => track.id === id);
      if (found)
        this.tracks = this.tracks.map((track) => (track.id === id ? update(track) : track));
      return {
        trackId: id,
        succeeded: found,
        message: found ? 'Updated in demo.' : 'Track not found.',
      };
    });
    return { results };
  }

  private imageDataUrl(image: string): string {
    const header = atob(image.slice(0, 64));
    const mime = header.startsWith('\x89PNG')
      ? 'image/png'
      : header.startsWith('\xff\xd8\xff')
        ? 'image/jpeg'
        : header.startsWith('GIF8')
          ? 'image/gif'
          : header.startsWith('RIFF') && header.slice(8, 12) === 'WEBP'
            ? 'image/webp'
            : header.trimStart().startsWith('<svg') || header.trimStart().startsWith('<?xml')
              ? 'image/svg+xml'
              : null;
    if (!mime) throw this.error(400, 'Unsupported demo image format.');
    return `data:${mime};base64,${image}`;
  }

  private nextId(items: { id: number }[]): number {
    return Math.max(0, ...items.map((item) => item.id)) + 1;
  }

  private error(status: number, message: string): HttpErrorResponse {
    return new HttpErrorResponse({ status, error: { message }, statusText: message });
  }

  private createTracks(): DemoTrack[] {
    let trackId = 1;
    return DEMO_ALBUMS.flatMap((album, albumIndex) =>
      album.songs.map((title, index) => {
        const image = new URL(`demo/${album.image}`, this.document.baseURI).href;
        const artistImage = new URL(`demo/${album.artistImage}`, this.document.baseURI).href;
        const description = `${album.artist} is a fictional artist from the Afterhours demo collection.`;
        return {
          id: trackId++,
          sourceRootId: 1,
          folderId: albumIndex + 1,
          relativePath: `${album.artist}/${album.title}/${String(index + 1).padStart(2, '0')} - ${title}.wav`,
          title,
          artist: album.artist,
          album: album.title,
          albumArtist: album.artist,
          genre: album.genre,
          trackNumber: index + 1,
          discNumber: 1,
          year: album.year,
          durationSeconds: 24,
          streamUrl: this.audioUrl(albumIndex),
          coverUrl: image,
          tileCoverUrl: image,
          albumCoverUrl: image,
          fileExtension: '.wav',
          albumDescription: `${album.title} is an imaginary release created for this demo.`,
          artistDescription: description,
          artistImageUrl: artistImage,
          artistImageMissing: false,
          albumArtistDescription: description,
          albumArtistImageUrl: artistImage,
          albumArtistImageMissing: false,
        };
      }),
    );
  }

  private audioUrl(albumIndex: number): string {
    const existing = this.audioUrls.get(albumIndex);
    if (existing) return existing;
    const rate = 16000;
    const samples = rate * 24;
    const buffer = new ArrayBuffer(44 + samples * 2);
    const view = new DataView(buffer);
    const text = (offset: number, value: string) => {
      for (let index = 0; index < value.length; index++)
        view.setUint8(offset + index, value.charCodeAt(index));
    };
    text(0, 'RIFF');
    view.setUint32(4, 36 + samples * 2, true);
    text(8, 'WAVE');
    text(12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, 1, true);
    view.setUint32(24, rate, true);
    view.setUint32(28, rate * 2, true);
    view.setUint16(32, 2, true);
    view.setUint16(34, 16, true);
    text(36, 'data');
    view.setUint32(40, samples * 2, true);
    const notes = [0, 4, 7, 11, 7, 4, 2, 7, 9, 7, 4, 2, 0, 4, 7, 4];
    for (let i = 0; i < samples; i++) {
      const time = i / rate;
      const noteTime = time % 0.75;
      const note = notes[Math.floor(time / 0.75) % notes.length] + albumIndex * 2;
      const frequency = 220 * 2 ** (note / 12);
      const envelope =
        Math.min(1, noteTime * 12) * Math.exp(-noteTime * 2.5) * Math.min(1, (24 - time) * 2);
      const sound =
        Math.sin(2 * Math.PI * frequency * time) +
        0.25 * Math.sin(2 * Math.PI * frequency * 2 * time);
      view.setInt16(44 + i * 2, sound * envelope * 7000, true);
    }
    const url = URL.createObjectURL(new Blob([buffer], { type: 'audio/wav' }));
    this.audioUrls.set(albumIndex, url);
    return url;
  }
}

export const demoInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith(`${MUSIC_API_BASE}/`)) return next(request);
  try {
    return of(new HttpResponse({ status: 200, body: inject(DemoBackend).handle(request) }));
  } catch (error) {
    return throwError(() => error);
  }
};
