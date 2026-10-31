import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthState, UserRole } from './auth-state';
import { MUSIC_API_BASE, MUSIC_API_PREFIX } from '../utils/api-path';

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
  fileExtension: string;
  albumDescription: string;
  artistDescription: string;
  artistImageUrl: string;
  artistImageMissing: boolean;
  albumArtistDescription: string;
  albumArtistImageUrl: string;
  albumArtistImageMissing: boolean;
}

export interface PagedTracks {
  items: MusicTrack[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface AlbumSummary {
  album: string;
  artist: string;
  trackCount: number;
  durationSeconds: number;
  coverTrackId: number;
  coverUrl: string;
}

export interface ArtistSummary {
  artist: string;
  trackCount: number;
  albumCount: number;
  coverUrl: string;
  artistImageUrl: string;
  artistImageMissing: boolean;
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

export type LibraryOperationType = 'scan' | 'metadata-refresh';
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

  listTracks(): Promise<MusicTrack[]> {
    return firstValueFrom(
      this.http.get<MusicTrack[]>(`${MUSIC_API_BASE}/library/tracks`, { withCredentials: true }),
    ).then((tracks) => this.mapTracks(tracks));
  }

  searchTracks(query: string, folderScope?: TrackFolderScope | null): Promise<MusicTrack[]> {
    const params = new URLSearchParams({ query });
    if (folderScope) {
      params.set('sourceRootId', String(folderScope.sourceRootId));
      params.set('folderPath', folderScope.path);
    }
    return firstValueFrom(
      this.http.get<MusicTrack[]>(`${MUSIC_API_BASE}/library/tracks/search?${params}`, {
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
      this.http.get<PagedTracks>(`${MUSIC_API_BASE}/library/tracks/page?${params}`, {
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
        coverUrl: `${MUSIC_API_PREFIX}${album.coverUrl}`,
      })),
    }));
  }

  listAlbumTracks(artist: string, album: string): Promise<MusicTrack[]> {
    const params = new URLSearchParams({ artist, album });
    return firstValueFrom(
      this.http.get<MusicTrack[]>(`${MUSIC_API_BASE}/library/albums/tracks?${params}`, {
        withCredentials: true,
      }),
    ).then((tracks) => this.mapTracks(tracks));
  }

  listAlbumTracksByTrackId(trackId: number): Promise<MusicTrack[]> {
    return firstValueFrom(
      this.http.get<MusicTrack[]>(
        `${MUSIC_API_BASE}/library/albums/by-track/${trackId}/tracks`,
        { withCredentials: true },
      ),
    ).then((tracks) => this.mapTracks(tracks));
  }

  listArtistTracks(artist: string): Promise<MusicTrack[]> {
    const params = new URLSearchParams({ artist });
    return firstValueFrom(
      this.http.get<MusicTrack[]>(`${MUSIC_API_BASE}/library/artists/tracks?${params}`, {
        withCredentials: true,
      }),
    ).then((tracks) => this.mapTracks(tracks));
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
        coverUrl: `${MUSIC_API_PREFIX}${artist.coverUrl}`,
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

  async updateDescription(
    artist: string,
    album: string | null,
    description: string,
  ): Promise<void> {
    const path = album === null ? 'artists' : 'albums';
    await firstValueFrom(
      this.http.put<void>(
        `${MUSIC_API_BASE}/admin/metadata/${path}/description`,
        { artist, album: album ?? '', description },
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

  private mapTracks(tracks: MusicTrack[]): MusicTrack[] {
    return tracks.map((track) => ({
      ...track,
      streamUrl: `${MUSIC_API_PREFIX}${track.streamUrl}`,
      coverUrl: `${MUSIC_API_PREFIX}${track.coverUrl}`,
      artistImageUrl: track.artistImageUrl ? `${MUSIC_API_PREFIX}${track.artistImageUrl}` : '',
      albumArtistImageUrl: track.albumArtistImageUrl
        ? `${MUSIC_API_PREFIX}${track.albumArtistImageUrl}`
        : '',
    }));
  }
}
