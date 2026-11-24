import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthState, UserRole } from './auth-state';
import { DEMO_MODE, MUSIC_API_BASE, MUSIC_API_PREFIX } from '../utils/api-path';

export interface MusicTrack {
  id: number;
  sourceRootId: number;
  folderId: number;
  relativePath: string;
  title: string;
  artist: string;
  album: string;
  albumArtist: string;
  genre: string;
  trackNumber: number;
  discNumber: number;
  year: number;
  durationSeconds: number;
  streamUrl: string;
  coverUrl: string;
  tileCoverUrl: string;
  albumCoverUrl: string;
  fileExtension: string;
}

type CoverImageVariant = 'tile' | 'track' | 'album';

export interface ArtistMetadata {
  description: string;
  imageUrl: string | null;
  imageMissing: boolean;
  referencedArtists: string[];
}

export interface PagedTracks {
  items: MusicTrack[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export type ListeningStatus = 'all' | 'recent' | 'progress' | 'unplayed';

export interface GenreSource {
  name: string;
  fromTags: boolean;
  fromAnalysis: boolean;
}

export type MusicTrackResponse = Omit<MusicTrack, 'tileCoverUrl' | 'albumCoverUrl'>;
type PagedTrackResponse = Omit<PagedTracks, 'items'> & { items: MusicTrackResponse[] };

export interface AlbumSummary {
  album: string;
  artist: string;
  releaseYear: number | null;
  trackCount: number;
  durationSeconds: number;
  coverTrackId: number;
  coverUrl: string;
  fileExtensions: string[];
  importedAt: string | null;
}

export interface ArtistSummary {
  artist: string;
  trackCount: number;
  albumCount: number;
  coverUrl: string;
  artistImageUrl: string;
  artistImageMissing: boolean;
  importedAt: string | null;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface MusicFolder {
  id: number;
  sourceRootId: number;
  name: string;
  relativePath: string;
}

export interface MusicFolderBrowseEntry {
  sourceRootId: number;
  name: string;
  relativePath: string;
}

export interface MusicFolderFile {
  name: string;
  relativePath: string;
}

export interface MusicFolderContents {
  sourceRootId: number;
  rootName: string;
  relativePath: string;
  folders: MusicFolderBrowseEntry[];
  files: MusicFolderFile[];
}

export interface TrackFolderScope {
  sourceRootId: number;
  path: string;
}

export interface SourceRoot {
  id: number;
  name: string;
  path: string;
  trackCount: number;
  isEnabled: boolean;
}

export interface UnscannedFolder {
  id: number;
  name: string;
  path: string;
}

export interface ExistingAlbum {
  sourceRootId: number;
  artist: string;
  album: string;
  relativePath: string;
}

export interface UnscannedAlbumCheck {
  hasCueFile: boolean;
  destinationFolderExists: boolean;
  existingAlbums: ExistingAlbum[];
}

export interface ImportUnscannedAlbum {
  relativePath: string;
  destinationRootId: number;
  artist: string;
  album: string;
  conflictAction: '' | 'rename' | 'delete';
  existingAlbumNewName: string;
  splitCue: boolean;
  keepCueSources: boolean;
}

export type LibraryOperationType = 'scan' | 'metadata-refresh' | 'import-scan-metadata' | 'database-cleanup';
export type LibraryOperationState = 'queued' | 'running' | 'completed' | 'failed';

export interface LibraryOperationStatus {
  id: string;
  sourceRootId: number;
  rootName: string;
  operationType: LibraryOperationType;
  state: LibraryOperationState;
  message: string;
  completedUnits: number;
  totalUnits: number;
  startedAt: string;
  finishedAt: string | null;
}

export interface MetadataRefreshResult {
  found: boolean;
  musicBrainzId: string;
  description: string;
  imageUrl: string | null;
  imageMissing: boolean;
  updatedAt: string;
  imageUpdated: boolean;
  imageProvider: string | null;
}

export interface MetadataRefreshPreview {
  found: boolean;
  musicBrainzId: string;
  description: string;
  imageDataUrl: string | null;
  imageProvider: string | null;
}

export interface MutationResult {
  trackId: number;
  succeeded: boolean;
  message: string;
}

export interface ManagedUser {
  id: number;
  email: string;
  role: UserRole;
  isEnabled: boolean;
  createdAt: string;
}

export interface MetadataPatch {
  title?: string;
  artist?: string;
  album?: string;
  albumArtist?: string;
  genre?: string;
  trackNumber?: number;
  discNumber?: number;
  year?: number;
}

@Injectable({ providedIn: 'root' })
export class MusicLibraryApi {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthState);

  listGenres(): Promise<string[]> {
    return firstValueFrom(
      this.http.get<string[]>(`${MUSIC_API_BASE}/playback/genres`, { withCredentials: true }),
    );
  }

  listGenreSources(): Promise<GenreSource[]> {
    return firstValueFrom(
      this.http.get<GenreSource[]>(`${MUSIC_API_BASE}/playback/genres/sources`, { withCredentials: true }),
    );
  }

  browseListeningTracks(
    query: string, genre: string, status: ListeningStatus, page: number, pageSize: number,
  ): Promise<PagedTracks> {
    const params = new URLSearchParams({ query, genre, status, page: String(page), pageSize: String(pageSize) });
    return firstValueFrom(
      this.http.get<PagedTrackResponse>(`${MUSIC_API_BASE}/playback/browse?${params}`, { withCredentials: true }),
    ).then((result) => ({ ...result, items: this.mapTracks(result.items) }));
  }

  listTracks(): Promise<MusicTrack[]> {
    return firstValueFrom(
      this.http.get<MusicTrackResponse[]>(`${MUSIC_API_BASE}/library/tracks`, {
        withCredentials: true,
      }),
    ).then((tracks) => this.mapTracks(tracks));
  }

  randomPlaylist(kind: 'random-albums' | 'album-radio' | 'random-tracks' | 'track-radio'): Promise<MusicTrack[]> {
    return firstValueFrom(
      this.http.get<MusicTrackResponse[]>(`${MUSIC_API_BASE}/playlists/random/${kind}`, { withCredentials: true }),
    ).then((tracks) => this.mapTracks(tracks));
  }

  searchTracks(query: string, folderScope?: TrackFolderScope | null): Promise<MusicTrack[]> {
    const params = new URLSearchParams({ query });
    if (folderScope) {
      params.set('sourceRootId', String(folderScope.sourceRootId));
      params.set('folderPath', folderScope.path);
    }
    return firstValueFrom(
      this.http.get<MusicTrackResponse[]>(`${MUSIC_API_BASE}/library/tracks/search?${params}`, {
        withCredentials: true,
      }),
    ).then((tracks) => this.mapTracks(tracks));
  }

  listTrackPage(
    query: string,
    page: number,
    pageSize: number,
    random = false,
    folderScope?: TrackFolderScope | null,
  ): Promise<PagedTracks> {
    const params = new URLSearchParams({
      query,
      page: String(page),
      pageSize: String(pageSize),
    });
    if (random) params.set('random', 'true');
    if (folderScope) {
      params.set('sourceRootId', String(folderScope.sourceRootId));
      params.set('folderPath', folderScope.path);
    }
    return firstValueFrom(
      this.http.get<PagedTrackResponse>(`${MUSIC_API_BASE}/library/tracks/page?${params}`, {
        withCredentials: true,
      }),
    ).then((result) => ({
      ...result,
      items: this.mapTracks(result.items),
    }));
  }

  listAlbumPage(
    query: string,
    page: number,
    pageSize: number,
    random = false,
  ): Promise<PagedResult<AlbumSummary>> {
    const params = new URLSearchParams({
      query,
      page: String(page),
      pageSize: String(pageSize),
    });
    if (random) params.set('random', 'true');
    return firstValueFrom(
      this.http.get<PagedResult<AlbumSummary>>(`${MUSIC_API_BASE}/library/albums/page?${params}`, {
        withCredentials: true,
      }),
    ).then((result) => ({
      ...result,
      items: result.items.map((album) => ({
        ...album,
        coverUrl: coverImageUrl(album.coverUrl, 'tile'),
      })),
    }));
  }

  listRecentAlbums(): Promise<AlbumSummary[]> {
    return firstValueFrom(
      this.http.get<AlbumSummary[]>(`${MUSIC_API_BASE}/library/albums/recent`, {
        withCredentials: true,
      }),
    ).then((albums) => albums.map((album) => ({
      ...album,
      coverUrl: coverImageUrl(album.coverUrl, 'tile'),
    })));
  }

  listAlbumTracks(artist: string, album: string): Promise<MusicTrack[]> {
    const params = new URLSearchParams({ artist, album });
    return firstValueFrom(
      this.http.get<MusicTrackResponse[]>(`${MUSIC_API_BASE}/library/albums/tracks?${params}`, {
        withCredentials: true,
      }),
    ).then((tracks) => this.mapTracks(tracks));
  }

  listAlbumTracksByTrackId(trackId: number): Promise<MusicTrack[]> {
    return firstValueFrom(
      this.http.get<MusicTrackResponse[]>(
        `${MUSIC_API_BASE}/library/albums/by-track/${trackId}/tracks`,
        { withCredentials: true },
      ),
    ).then((tracks) => this.mapTracks(tracks));
  }

  listArtistTracks(artist: string): Promise<MusicTrack[]> {
    const params = new URLSearchParams({ artist });
    return firstValueFrom(
      this.http.get<MusicTrackResponse[]>(`${MUSIC_API_BASE}/library/artists/tracks?${params}`, {
        withCredentials: true,
      }),
    ).then((tracks) => this.mapTracks(tracks));
  }

  getArtistMetadata(artist: string): Promise<ArtistMetadata> {
    const params = new URLSearchParams({ artist });
    return firstValueFrom(
      this.http.get<ArtistMetadata>(`${MUSIC_API_BASE}/library/artists/metadata?${params}`, {
        withCredentials: true,
      }),
    ).then((metadata) => ({
      ...metadata,
      imageUrl: metadata.imageUrl ? `${MUSIC_API_PREFIX}${metadata.imageUrl}` : null,
    }));
  }

  listArtistPage(
    query: string,
    page: number,
    pageSize: number,
    random = false,
  ): Promise<PagedResult<ArtistSummary>> {
    const params = new URLSearchParams({
      query,
      page: String(page),
      pageSize: String(pageSize),
    });
    if (random) params.set('random', 'true');
    return firstValueFrom(
      this.http.get<PagedResult<ArtistSummary>>(`${MUSIC_API_BASE}/library/artists/page?${params}`, {
        withCredentials: true,
      }),
    ).then((result) => ({
      ...result,
      items: result.items.map((artist) => ({
        ...artist,
        coverUrl: coverImageUrl(artist.coverUrl, 'tile'),
        artistImageUrl: artist.artistImageUrl
          ? `${MUSIC_API_PREFIX}${artist.artistImageUrl}`
          : '',
      })),
    }));
  }

  listRoots(): Promise<SourceRoot[]> {
    return firstValueFrom(
      this.http.get<SourceRoot[]>(`${MUSIC_API_BASE}/admin/roots`, { withCredentials: true }),
    );
  }

  listTrackMoveFolders(): Promise<MusicFolder[]> {
    return firstValueFrom(
      this.http.get<MusicFolder[]>(`${MUSIC_API_BASE}/admin/roots/folders`, {
        withCredentials: true,
      }),
    );
  }

  listLibraryFolderRoots(): Promise<MusicFolderBrowseEntry[]> {
    return firstValueFrom(
      this.http.get<MusicFolderBrowseEntry[]>(`${MUSIC_API_BASE}/library/folders`, {
        withCredentials: true,
      }),
    );
  }

  browseLibraryFolder(sourceRootId: number, path: string): Promise<MusicFolderContents> {
    const params = new URLSearchParams({ path });
    return firstValueFrom(
      this.http.get<MusicFolderContents>(
        `${MUSIC_API_BASE}/library/folders/${sourceRootId}?${params}`,
        { withCredentials: true },
      ),
    );
  }

  listUnscannedFolders(): Promise<UnscannedFolder[]> {
    return firstValueFrom(
      this.http.get<UnscannedFolder[]>(`${MUSIC_API_BASE}/admin/unscanned-folders`, {
        withCredentials: true,
      }),
    );
  }

  async addUnscannedFolder(name: string, path: string): Promise<UnscannedFolder> {
    return firstValueFrom(
      this.http.post<UnscannedFolder>(
        `${MUSIC_API_BASE}/admin/unscanned-folders`,
        { name, path },
        await this.writeOptions(),
      ),
    );
  }

  async removeUnscannedFolder(id: number): Promise<void> {
    await firstValueFrom(
      this.http.delete<void>(
        `${MUSIC_API_BASE}/admin/unscanned-folders/${id}`,
        await this.writeOptions(),
      ),
    );
  }

  browseUnscannedFolder(rootId: number, path: string): Promise<MusicFolderContents> {
    const params = new URLSearchParams({ path });
    return firstValueFrom(
      this.http.get<MusicFolderContents>(
        `${MUSIC_API_BASE}/admin/unscanned-folders/${rootId}?${params}`,
        { withCredentials: true },
      ),
    );
  }

  async renameUnscannedFolder(rootId: number, path: string, name: string): Promise<void> {
    const params = new URLSearchParams({ path });
    await firstValueFrom(
      this.http.put<void>(
        `${MUSIC_API_BASE}/admin/unscanned-folders/${rootId}/folders?${params}`,
        { name },
        await this.writeOptions(),
      ),
    );
  }

  async renameUnscannedTrack(rootId: number, path: string, name: string): Promise<void> {
    const params = new URLSearchParams({ path });
    await firstValueFrom(
      this.http.put<void>(
        `${MUSIC_API_BASE}/admin/unscanned-folders/${rootId}/tracks?${params}`,
        { name },
        await this.writeOptions(),
      ),
    );
  }

  checkUnscannedAlbum(
    rootId: number,
    relativePath: string,
    artist: string,
    album: string,
    destinationRootId: number,
  ): Promise<UnscannedAlbumCheck> {
    return firstValueFrom(
      this.http.post<UnscannedAlbumCheck>(
        `${MUSIC_API_BASE}/admin/unscanned-folders/${rootId}/check-album`,
        { relativePath, artist, album, destinationRootId },
        { withCredentials: true },
      ),
    );
  }

  async importUnscannedAlbum(
    rootId: number,
    request: ImportUnscannedAlbum,
  ): Promise<LibraryOperationStatus> {
    return firstValueFrom(
      this.http.post<LibraryOperationStatus>(
        `${MUSIC_API_BASE}/admin/unscanned-folders/${rootId}/import`,
        request,
        await this.writeOptions(),
      ),
    );
  }

  async addRoot(name: string, path: string): Promise<SourceRoot> {
    return firstValueFrom(
      this.http.post<SourceRoot>(
        `${MUSIC_API_BASE}/admin/roots`,
        { name, path },
        await this.writeOptions(),
      ),
    );
  }

  async scanRoot(id: number): Promise<LibraryOperationStatus> {
    return firstValueFrom(
      this.http.post<LibraryOperationStatus>(
        `${MUSIC_API_BASE}/admin/roots/${id}/scan`,
        {},
        await this.writeOptions(),
      ),
    );
  }

  async cleanDatabase(): Promise<LibraryOperationStatus> {
    return firstValueFrom(
      this.http.post<LibraryOperationStatus>(
        `${MUSIC_API_BASE}/admin/operations/clean-database`,
        {},
        await this.writeOptions(),
      ),
    );
  }

  async refreshRootMetadata(id: number): Promise<LibraryOperationStatus> {
    return firstValueFrom(
      this.http.post<LibraryOperationStatus>(
        `${MUSIC_API_BASE}/admin/roots/${id}/metadata-refresh`,
        {},
        await this.writeOptions(),
      ),
    );
  }

  getLibraryOperation(id: string): Promise<LibraryOperationStatus> {
    return firstValueFrom(
      this.http.get<LibraryOperationStatus>(`${MUSIC_API_BASE}/admin/operations/${id}`, {
        withCredentials: true,
      }),
    );
  }

  getActiveLibraryOperation(): Promise<LibraryOperationStatus | null> {
    return firstValueFrom(
      this.http.get<LibraryOperationStatus | null>(`${MUSIC_API_BASE}/admin/operations/active`, {
        withCredentials: true,
      }),
    );
  }

  async removeRoot(id: number): Promise<void> {
    await firstValueFrom(
      this.http.delete<void>(`${MUSIC_API_BASE}/admin/roots/${id}`, await this.writeOptions()),
    );
  }

  async updateMetadata(trackIds: number[], changes: MetadataPatch): Promise<MutationResult[]> {
    const body = { trackIds, changes };
    const response = await firstValueFrom(
      this.http.post<{ results: MutationResult[] }>(
        `${MUSIC_API_BASE}/admin/tracks/metadata`,
        body,
        await this.writeOptions(),
      ),
    );
    return response.results;
  }

  async updateCover(trackIds: number[], image: File): Promise<MutationResult[]> {
    const imageData = await this.imageAsBase64(image);
    const response = await firstValueFrom(
      this.http.post<{ results: MutationResult[] }>(
        `${MUSIC_API_BASE}/admin/tracks/cover`,
        { trackIds, image: imageData },
        await this.writeOptions(),
      ),
    );
    return response.results;
  }

  async updateArtistDescription(artist: string, description: string): Promise<void> {
    await firstValueFrom(
      this.http.put<void>(
        `${MUSIC_API_BASE}/admin/metadata/artists/description`,
        { artist, description },
        await this.writeOptions(),
      ),
    );
  }

  async refreshInformation(
    artist: string,
    album: string | null,
    providerId?: string,
    candidateId?: string,
  ): Promise<MetadataRefreshResult> {
    const path = album === null ? 'artists' : 'albums';
    const result = await firstValueFrom(
      this.http.post<MetadataRefreshResult>(
        `${MUSIC_API_BASE}/admin/metadata/${path}/refresh`,
        { artist, album: album ?? '', providerId: providerId ?? '', candidateId: candidateId ?? '' },
        await this.writeOptions(),
      ),
    );
    return {
      ...result,
      imageUrl: result.imageUrl ? `${MUSIC_API_PREFIX}${result.imageUrl}` : null,
    };
  }

  async previewAlbumMetadata(
    artist: string,
    album: string,
    providerId: string,
    candidateId: string,
  ): Promise<MetadataRefreshPreview> {
    return firstValueFrom(
      this.http.post<MetadataRefreshPreview>(
        `${MUSIC_API_BASE}/admin/metadata/albums/preview`,
        { artist, album, providerId, candidateId },
        await this.writeOptions(),
      ),
    );
  }

  async updateEntityImage(artist: string, album: string | null, image: File): Promise<void> {
    const path = album === null ? 'artists' : 'albums';
    const imageData = await this.imageAsBase64(image);
    await firstValueFrom(
      this.http.put<void>(
        `${MUSIC_API_BASE}/admin/metadata/${path}/image`,
        { artist, album: album ?? '', image: imageData },
        await this.writeOptions(),
      ),
    );
  }

  async moveTracks(trackIds: number[], destinationFolderId: number): Promise<MutationResult[]> {
    const response = await firstValueFrom(
      this.http.post<{ results: MutationResult[] }>(
        `${MUSIC_API_BASE}/admin/tracks/move`,
        { trackIds, destinationFolderId },
        await this.writeOptions(),
      ),
    );
    return response.results;
  }

  listUsers(): Promise<ManagedUser[]> {
    return firstValueFrom(
      this.http.get<ManagedUser[]>(`${MUSIC_API_BASE}/users`, { withCredentials: true }),
    );
  }

  async createUser(email: string, temporaryPassword: string, role: UserRole): Promise<ManagedUser> {
    return firstValueFrom(
      this.http.post<ManagedUser>(
        `${MUSIC_API_BASE}/users`,
        { email, temporaryPassword, role },
        await this.writeOptions(),
      ),
    );
  }

  async setUserRole(userId: number, role: UserRole): Promise<void> {
    await firstValueFrom(
      this.http.put<void>(
        `${MUSIC_API_BASE}/users/${userId}/role`,
        { role },
        await this.writeOptions(),
      ),
    );
  }

  async setUserEnabled(userId: number, isEnabled: boolean): Promise<void> {
    await firstValueFrom(
      this.http.put<void>(
        `${MUSIC_API_BASE}/users/${userId}/enabled`,
        { isEnabled },
        await this.writeOptions(),
      ),
    );
  }

  async resetPassword(userId: number, newPassword: string): Promise<void> {
    await firstValueFrom(
      this.http.put<void>(
        `${MUSIC_API_BASE}/users/${userId}/password`,
        { newPassword },
        await this.writeOptions(),
      ),
    );
  }

  private async writeOptions(): Promise<{
    headers: { 'X-CSRF-TOKEN': string };
    withCredentials: true;
  }> {
    return { headers: { 'X-CSRF-TOKEN': await this.auth.getCsrfToken() }, withCredentials: true };
  }

  private async imageAsBase64(image: File): Promise<string> {
    const bytes = new Uint8Array(await image.arrayBuffer());
    let binary = '';
    const chunkSize = 0x8000;
    for (let offset = 0; offset < bytes.length; offset += chunkSize) {
      binary += String.fromCharCode(...bytes.subarray(offset, offset + chunkSize));
    }
    return btoa(binary);
  }

  mapTracks(tracks: MusicTrackResponse[]): MusicTrack[] {
    return tracks.map((track) => {
      const coverUrl = coverImageUrl(track.coverUrl, 'track');
      return {
        ...track,
        streamUrl: `${MUSIC_API_PREFIX}${track.streamUrl}`,
        coverUrl,
        tileCoverUrl: coverImageUrl(track.coverUrl, 'tile'),
        albumCoverUrl: coverImageUrl(track.coverUrl, 'album'),
      };
    });
  }
}

function coverImageUrl(url: string, variant: CoverImageVariant): string {
  if (DEMO_MODE) return url;
  const [path, query = ''] = url.split('?', 2);
  const params = new URLSearchParams(query);
  params.set('variant', variant);
  return `${MUSIC_API_PREFIX}${path}?${params.toString()}`;
}
