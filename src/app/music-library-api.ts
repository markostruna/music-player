import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthState, UserRole } from './auth-state';

export interface MusicTrack {
  id: number;
  sourceRootId: number;
  folderId: number;
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
}

export interface MusicFolder {
  id: number;
  sourceRootId: number;
  name: string;
  relativePath: string;
}

export interface SourceRoot {
  id: number;
  name: string;
  path: string;
  trackCount: number;
  isEnabled: boolean;
}

export interface ScanSummary {
  sourceRootId: number;
  discoveredTracks: number;
  unreadableTracks: number;
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
    return firstValueFrom(this.http.get<MusicTrack[]>('/api/library/tracks', { withCredentials: true }));
  }

  listRoots(): Promise<SourceRoot[]> {
    return firstValueFrom(this.http.get<SourceRoot[]>('/api/admin/roots', { withCredentials: true }));
  }

  listFolders(): Promise<MusicFolder[]> {
    return firstValueFrom(this.http.get<MusicFolder[]>('/api/admin/roots/folders', { withCredentials: true }));
  }

  async addRoot(name: string, path: string): Promise<SourceRoot> {
    return firstValueFrom(this.http.post<SourceRoot>('/api/admin/roots', { name, path }, await this.writeOptions()));
  }

  async scanRoot(id: number): Promise<ScanSummary> {
    return firstValueFrom(this.http.post<ScanSummary>(`/api/admin/roots/${id}/scan`, {}, await this.writeOptions()));
  }

  async removeRoot(id: number): Promise<void> {
    await firstValueFrom(this.http.delete<void>(`/api/admin/roots/${id}`, await this.writeOptions()));
  }

  async updateMetadata(trackIds: number[], changes: MetadataPatch): Promise<MutationResult[]> {
    const body = { trackIds, changes };
    const response = await firstValueFrom(this.http.post<{ results: MutationResult[] }>(
      '/api/admin/tracks/metadata', body, await this.writeOptions()));
    return response.results;
  }

  async updateCover(trackIds: number[], image: File): Promise<MutationResult[]> {
    const bytes = Array.from(new Uint8Array(await image.arrayBuffer()));
    const response = await firstValueFrom(this.http.post<{ results: MutationResult[] }>(
      '/api/admin/tracks/cover', { trackIds, image: bytes }, await this.writeOptions()));
    return response.results;
  }

  async moveTracks(trackIds: number[], destinationFolderId: number): Promise<MutationResult[]> {
    const response = await firstValueFrom(this.http.post<{ results: MutationResult[] }>(
      '/api/admin/tracks/move', { trackIds, destinationFolderId }, await this.writeOptions()));
    return response.results;
  }

  listUsers(): Promise<ManagedUser[]> {
    return firstValueFrom(this.http.get<ManagedUser[]>('/api/users', { withCredentials: true }));
  }

  async createUser(email: string, temporaryPassword: string, role: UserRole): Promise<ManagedUser> {
    return firstValueFrom(this.http.post<ManagedUser>('/api/users',
      { email, temporaryPassword, role }, await this.writeOptions()));
  }

  async setUserRole(userId: number, role: UserRole): Promise<void> {
    await firstValueFrom(this.http.put<void>(`/api/users/${userId}/role`, { role }, await this.writeOptions()));
  }

  async setUserEnabled(userId: number, isEnabled: boolean): Promise<void> {
    await firstValueFrom(this.http.put<void>(`/api/users/${userId}/enabled`, { isEnabled }, await this.writeOptions()));
  }

  async resetPassword(userId: number, newPassword: string): Promise<void> {
    await firstValueFrom(this.http.put<void>(`/api/users/${userId}/password`, { newPassword }, await this.writeOptions()));
  }

  private async writeOptions(): Promise<{ headers: { 'X-CSRF-TOKEN': string }; withCredentials: true }> {
    return { headers: { 'X-CSRF-TOKEN': await this.auth.getCsrfToken() }, withCredentials: true };
  }
}
