import { HttpClient } from '@angular/common/http';
import { effect, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthState } from './auth-state';
import { MusicLibraryApi, MusicTrack, MusicTrackResponse } from './music-library-api';
import { MUSIC_API_BASE } from '../utils/api-path';

export interface Playlist {
  id: number;
  name: string;
  tracks: MusicTrack[];
  unavailableTrackCount: number;
}

interface PlaylistResponse {
  id: number;
  name: string;
  tracks: MusicTrackResponse[];
  unavailableTrackCount: number;
}

export const BUILT_IN_PLAYLISTS = [
  { id: 'random-albums', name: 'Random albums', description: 'Queue up to 10 random albums.' },
  { id: 'album-radio', name: 'Album radio', description: 'Keep playing random albums.' },
  { id: 'random-tracks', name: 'Random tracks', description: 'Queue up to 100 random tracks.' },
  { id: 'track-radio', name: 'Track radio', description: 'Keep playing random tracks.' },
] as const;

export type BuiltInPlaylistId = (typeof BUILT_IN_PLAYLISTS)[number]['id'];

@Injectable({ providedIn: 'root' })
export class PlaylistService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthState);
  private readonly library = inject(MusicLibraryApi);
  readonly playlists = signal<Playlist[]>([]);

  constructor() {
    effect(() => {
      this.auth.user();
      this.playlists.set([]);
    });
  }

  async load(): Promise<void> {
    const userId = this.auth.user()?.id;
    const response = await firstValueFrom(this.http.get<PlaylistResponse[]>(
      `${MUSIC_API_BASE}/playlists`, { withCredentials: true },
    ));
    if (this.auth.user()?.id === userId) this.playlists.set(response.map((item) => this.map(item)));
  }

  async get(id: number): Promise<Playlist> {
    return this.map(await firstValueFrom(this.http.get<PlaylistResponse>(
      `${MUSIC_API_BASE}/playlists/${id}`, { withCredentials: true },
    )));
  }

  async create(name: string): Promise<Playlist> {
    const token = await this.auth.getCsrfToken();
    const response = await firstValueFrom(this.http.post<PlaylistResponse>(
      `${MUSIC_API_BASE}/playlists`, { name, trackIds: [] },
      { headers: { 'X-CSRF-TOKEN': token }, withCredentials: true },
    ));
    const playlist = this.map(response);
    this.playlists.update((items) => [...items, playlist]);
    return playlist;
  }

  async update(playlist: Playlist): Promise<Playlist> {
    const token = await this.auth.getCsrfToken();
    const response = await firstValueFrom(this.http.put<PlaylistResponse>(
      `${MUSIC_API_BASE}/playlists/${playlist.id}`,
      { name: playlist.name, trackIds: playlist.tracks.map((track) => track.id) },
      { headers: { 'X-CSRF-TOKEN': token }, withCredentials: true },
    ));
    const updated = this.map(response);
    this.playlists.update((items) => items.map((item) => item.id === updated.id ? updated : item));
    return updated;
  }

  async delete(id: number): Promise<void> {
    const token = await this.auth.getCsrfToken();
    await firstValueFrom(this.http.delete(`${MUSIC_API_BASE}/playlists/${id}`,
      { headers: { 'X-CSRF-TOKEN': token }, withCredentials: true }));
    this.playlists.update((items) => items.filter((item) => item.id !== id));
  }

  private map(item: PlaylistResponse): Playlist {
    return { ...item, tracks: this.library.mapTracks(item.tracks) };
  }
}
