import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom, timeout } from 'rxjs';
import { MUSIC_API_BASE } from '../utils/api-path';
import { MusicTrackResponse } from './music-library-api';

export interface PlaybackDevice {
  id: string;
  name: string;
}

export interface PlaybackSnapshot {
  revision: number;
  positionRevision: number;
  playId: string;
  queue: MusicTrackResponse[] | null;
  currentIndex: number;
  positionSeconds: number;
  isPlaying: boolean;
  shuffle: boolean;
  repeat: boolean;
  ownerSessionId: string | null;
  transferTargetSessionId: string | null;
  devices: PlaybackDevice[];
}

export interface PlaybackCommand {
  sessionId: string;
  revision: number;
  action: string;
  trackIds?: number[];
  index?: number;
  positionSeconds?: number;
  targetSessionId?: string;
  wasPlaying?: boolean;
  startPlaying?: boolean;
  expectedPlayId?: string;
  startPositionSeconds?: number;
}

export interface PlayedTrackResponse {
  track: MusicTrackResponse;
  playCount: number;
  positionSeconds: number;
  lastPlayedAt: string;
}

export interface PlaybackStatisticsResponse {
  recentlyPlayed: PlayedTrackResponse[];
  continueListening: PlayedTrackResponse[];
}

@Injectable({ providedIn: 'root' })
export class PlaybackSyncApi {
  private readonly http = inject(HttpClient);
  private lastToken: string | null = null;

  async statistics(): Promise<PlaybackStatisticsResponse> {
    return firstValueFrom(
      this.http.get<PlaybackStatisticsResponse>(`${MUSIC_API_BASE}/playback/statistics`, {
        withCredentials: true,
      }).pipe(timeout(5000)),
    );
  }

  async poll(request: {
    sessionId: string;
    deviceName: string;
    revision: number;
    positionSeconds: number;
    isPlaying: boolean;
    transferStopped: boolean;
    playId: string;
  }): Promise<PlaybackSnapshot> {
    const token = await this.getToken();
    return firstValueFrom(
      this.http
        .post<PlaybackSnapshot>(`${MUSIC_API_BASE}/playback/poll`, request, {
          headers: { 'X-CSRF-TOKEN': token },
          withCredentials: true,
        })
        .pipe(timeout(5000)),
    );
  }

  async command(request: PlaybackCommand): Promise<PlaybackSnapshot> {
    const token = await this.getToken();
    return firstValueFrom(
      this.http
        .post<PlaybackSnapshot>(`${MUSIC_API_BASE}/playback/command`, request, {
          headers: { 'X-CSRF-TOKEN': token },
          withCredentials: true,
        })
        .pipe(timeout(5000)),
    );
  }

  private async getToken(): Promise<string> {
    const result = await firstValueFrom(
      this.http
        .get<{ token: string }>(`${MUSIC_API_BASE}/auth/csrf`, { withCredentials: true })
        .pipe(timeout(5000)),
    );
    this.lastToken = result.token;
    return result.token;
  }

  leaveOnPageHide(request: PlaybackCommand): void {
    if (!this.lastToken) return;
    void fetch(`${MUSIC_API_BASE}/playback/command`, {
      method: 'POST',
      credentials: 'same-origin',
      keepalive: true,
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': this.lastToken },
      body: JSON.stringify(request),
    })
      .then((response) => {
        if (!response.ok)
          console.warn('Could not save playback when closing the page.', response.status);
      })
      .catch((error: unknown) =>
        console.warn('Could not save playback when closing the page.', error),
      );
  }
}
