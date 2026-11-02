import { Routes } from '@angular/router';
import { requireAdmin } from '@shared/guards/admin.guard';

export const adminRoutes: Routes = [
  {
    path: 'admin',
    canActivate: [requireAdmin],
    loadComponent: () => import('./admin-page/admin-page').then((module) => module.AdminPage),
  },
];
