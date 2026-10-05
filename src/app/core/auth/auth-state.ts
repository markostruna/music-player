import { DOCUMENT } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MUSIC_API_BASE } from '../api/api-path';

export type UserRole = 'Admin' | 'Guest';
export type ThemeName = 'Light' | 'Dark' | 'Blue';

export interface SignedInUser {
  id: number;
  email: string;
  role: UserRole;
  theme: ThemeName;
}

@Injectable({ providedIn: 'root' })
export class AuthState {
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);
  readonly user = signal<SignedInUser | null>(null);
  readonly isAuthenticated = computed(() => this.user() !== null);
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');

  async restore(): Promise<boolean> {
    try {
      const user = await firstValueFrom(this.http.get<SignedInUser>(`${MUSIC_API_BASE}/auth/me`, { withCredentials: true }));
      this.applyUser(user);
      return true;
    } catch {
      this.applyUser(null);
      return false;
    }
  }

  async signIn(email: string, password: string): Promise<void> {
    const token = await this.getCsrfToken();
    const user = await firstValueFrom(this.http.post<SignedInUser>(
      `${MUSIC_API_BASE}/auth/login`,
      { email, password },
      { headers: { 'X-CSRF-TOKEN': token }, withCredentials: true },
    ));
    this.applyUser(user);
  }

  async signOut(): Promise<void> {
    try {
      const token = await this.getCsrfToken();
      await firstValueFrom(this.http.post(`${MUSIC_API_BASE}/auth/logout`, {}, {
        headers: { 'X-CSRF-TOKEN': token },
        withCredentials: true,
      }));
    } finally {
      this.applyUser(null);
    }
  }

  async setTheme(theme: ThemeName): Promise<void> {
    const token = await this.getCsrfToken();
    const user = await firstValueFrom(this.http.put<SignedInUser>(
      `${MUSIC_API_BASE}/auth/theme`,
      { theme },
      { headers: { 'X-CSRF-TOKEN': token }, withCredentials: true },
    ));
    this.applyUser(user);
  }

  async getCsrfToken(): Promise<string> {
    const result = await firstValueFrom(this.http.get<{ token: string }>(`${MUSIC_API_BASE}/auth/csrf`, { withCredentials: true }));
    return result.token;
  }

  private applyUser(user: SignedInUser | null): void {
    this.user.set(user);
    this.document.documentElement.dataset['theme'] = (user?.theme ?? 'Light').toLowerCase();
  }
}
