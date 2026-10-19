import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AuthState } from '../services/auth-state';
import { MUSIC_API_BASE } from '../utils/api-path';

export const apiErrorInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthState);
  const router = inject(Router);
  const isAuthRequest = request.url.startsWith(`${MUSIC_API_BASE}/auth/`);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || isAuthRequest) {
        return throwError(() => error);
      }

      let refreshCompleted = false;
      return from(auth.refreshSession()).pipe(
        switchMap(() => {
          refreshCompleted = true;
          return next(request);
        }),
        catchError((refreshOrRetryError: unknown) => {
          if (
            !refreshCompleted ||
            (refreshOrRetryError instanceof HttpErrorResponse && refreshOrRetryError.status === 401)
          ) {
            auth.clearSession();
            void router.navigateByUrl('/login');
          }
          return throwError(() => refreshOrRetryError);
        }),
      );
    }),
  );
};
