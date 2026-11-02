import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthState } from './auth-state';
import { MUSIC_API_BASE } from '../utils/api-path';

export interface ProviderCredentialStatus {
  key: string;
  label: string;
  required: boolean;
  isConfigured: boolean;
}

export interface ProviderSettingsItem {
  id: string;
  name: string;
  description: string;
  isConfigured: boolean;
  credentials: ProviderCredentialStatus[];
}

export interface DefaultAlbumProvider {
  providerId: string;
}

@Injectable({ providedIn: 'root' })
export class ProviderSettingsApi {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthState);

  list(): Promise<ProviderSettingsItem[]> {
    return firstValueFrom(
      this.http.get<ProviderSettingsItem[]>(`${MUSIC_API_BASE}/admin/providers`, {
        withCredentials: true,
      }),
    );
  }

  getDefaultAlbumProvider(): Promise<DefaultAlbumProvider> {
    return firstValueFrom(
      this.http.get<DefaultAlbumProvider>(`${MUSIC_API_BASE}/admin/providers/default-album-provider`, {
        withCredentials: true,
      }),
    );
  }

  async updateDefaultAlbumProvider(providerId: string): Promise<DefaultAlbumProvider> {
    const token = await this.auth.getCsrfToken();
    return firstValueFrom(
      this.http.put<DefaultAlbumProvider>(
        `${MUSIC_API_BASE}/admin/providers/default-album-provider`,
        { providerId },
        {
          headers: { 'X-CSRF-TOKEN': token },
          withCredentials: true,
        },
      ),
    );
  }

  async update(
    providerId: string,
    credentials: Record<string, string>,
  ): Promise<ProviderSettingsItem[]> {
    const token = await this.auth.getCsrfToken();
    return firstValueFrom(
      this.http.put<ProviderSettingsItem[]>(
        `${MUSIC_API_BASE}/admin/providers/${encodeURIComponent(providerId)}`,
        { credentials },
        {
          headers: { 'X-CSRF-TOKEN': token },
          withCredentials: true,
        },
      ),
    );
  }
}
