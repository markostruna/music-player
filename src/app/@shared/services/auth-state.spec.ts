import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthState } from './auth-state';
import { MUSIC_API_BASE } from '../utils/api-path';

describe('AuthState', () => {
  let auth: AuthState;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthState);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('treats an unauthorized session restore as signed out', async () => {
    const restored = auth.restore();
    http.expectOne(`${MUSIC_API_BASE}/auth/me`).flush(
      {},
      { status: 401, statusText: 'Unauthorized' },
    );

    await expect(restored).resolves.toBe(false);
    expect(auth.user()).toBeNull();
  });

  it('surfaces server errors instead of converting them to signed-out state', async () => {
    const restored = auth.restore();
    http.expectOne(`${MUSIC_API_BASE}/auth/me`).flush(
      {},
      { status: 503, statusText: 'Service Unavailable' },
    );

    await expect(restored).rejects.toBeInstanceOf(HttpErrorResponse);
  });
});
