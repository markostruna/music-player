import { Routes } from '@angular/router';
import { requireAdmin } from '@shared/guards/admin.guard';
import { AdminPage } from './admin-page/admin-page';

export const adminRoutes: Routes = [
  { path: 'admin', component: AdminPage, canActivate: [requireAdmin] },
];
