import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthState } from './auth-state';
import { MUSIC_API_BASE, MUSIC_API_PREFIX } from '../utils/api-path';

export interface IdentifyProvider {
  id: string;
  name: string;
  isConfigured: boolean;
}

export interface AlbumMatch {
  provider: string;
  id: string;
  artist: string;
  title: string;
  year: number | null;
  trackCount: number | null;
  coverUrl: string | null;
  detail: string | null;
}

export interface AlbumMatchTrack {
  discNumber: number;
  trackNumber: number;
  title: string;
  artist: string;
  durationSeconds: number | null;
}

export interface AlbumMatchDetail {
  match: AlbumMatch;
  genre: string;
  musicBrainzReleaseGroupId: string | null;
  tracks: AlbumMatchTrack[];
}

export interface AlbumSuggestions {
  tagArtist: string | null;
  tagAlbum: string | null;
  tagYear: number | null;
  matches: AlbumMatch[];
}

export interface CoverCandidate {
  id: string;
  source: string;
  label: string;
  previewUrl: string;
}

export interface AlbumCovers {
  candidates: CoverCandidate[];
  warnings: string[];
}

export interface AlbumMovePlan {
  canMove: boolean;
  reason: string | null;
  sourcePath: string;
  destinationPath: string;
}

export interface AlbumMoveRequest {
  trackIds: number[];
  destinationRootId: number;
  artist: string;
  album: string;
  year: number | null;
}

@Injectable({ providedIn: 'root' })
export class AlbumIdentifyApi {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthState);
  private readonly base = `${MUSIC_API_BASE}/admin/identify`;

  listProviders(): Promise<IdentifyProvider[]> {
    return firstValueFrom(
      this.http.get<IdentifyProvider[]>(`${this.base}/providers`, { withCredentials: true }),
    );
  }

  async suggest(trackIds: number[], provider: string): Promise<AlbumSuggestions> {
    return this.post<AlbumSuggestions>('suggestions', { trackIds, provider });
  }

  async search(
    provider: string,
    query: { artist?: string; album?: string; year?: number | null },
  ): Promise<AlbumMatch[]> {
    return this.post<AlbumMatch[]>('search', {
      provider,
      artist: query.artist || null,
      album: query.album || null,
      year: query.year || null,
    });
  }

  getMatch(provider: string, id: string): Promise<AlbumMatchDetail> {
    return firstValueFrom(
      this.http.get<AlbumMatchDetail>(
        `${this.base}/matches/${encodeURIComponent(provider)}/${encodeURIComponent(id)}`,
        { withCredentials: true },
      ),
    );
  }

  async covers(trackIds: number[], provider: string, matchId: string | null): Promise<AlbumCovers> {
    const result = await this.post<AlbumCovers>('covers', { trackIds, provider, matchId });
    return {
      ...result,
      candidates: result.candidates.map((candidate) => ({
        ...candidate,
        previewUrl: MUSIC_API_PREFIX + candidate.previewUrl,
      })),
    };
  }

  remoteImageUrl(url: string): string {
    return `${MUSIC_API_BASE}/admin/identify/image?url=${encodeURIComponent(url)}`;
  }

  previewMove(request: AlbumMoveRequest): Promise<AlbumMovePlan> {
    return this.post<AlbumMovePlan>('move/preview', request);
  }

  move(request: AlbumMoveRequest): Promise<{ newPath: string; movedTracks: number }> {
    return this.post('move', request);
  }

  async fetchImage(url: string): Promise<File> {
    const response = await fetch(url, { credentials: 'include' });
    if (!response.ok) throw new Error('The image could not be loaded.');
    const blob = await response.blob();
    const extension = blob.type.split('/')[1] ?? 'jpg';
    return new File([blob], `cover.${extension}`, { type: blob.type });
  }

  private async post<T>(path: string, body: unknown): Promise<T> {
    return firstValueFrom(
      this.http.post<T>(`${this.base}/${path}`, body, {
        headers: { 'X-CSRF-TOKEN': await this.auth.getCsrfToken() },
        withCredentials: true,
      }),
    );
  }
}
