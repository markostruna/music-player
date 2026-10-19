import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthState } from '../services/auth-state';

export const requireAuth: CanActivateFn = async () => {
  const auth = inject(AuthState);
  const router = inject(Router);
  return (await auth.restore()) || router.parseUrl('/login');
};
