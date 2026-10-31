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
