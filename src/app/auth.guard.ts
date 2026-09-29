import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthState } from './auth-state';

export const requireAuth: CanActivateFn = async () => {
  const auth = inject(AuthState);
  return await auth.restore() || inject(Router).parseUrl('/login');
};

export const requireAdmin: CanActivateFn = async () => {
  const auth = inject(AuthState);
  const router = inject(Router);
  if (!await auth.restore()) return router.parseUrl('/login');
  return auth.isAdmin() || router.parseUrl('/library');
};
