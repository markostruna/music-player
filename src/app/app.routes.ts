import { Routes } from '@angular/router';
import { adminRoutes } from './admin/admin.routes';
import { loginRoutes } from './login/login.routes';
import { shellRoutes } from './shell/shell.routes';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'login' },
  ...loginRoutes,
  ...shellRoutes,
  ...adminRoutes,
  { path: '**', redirectTo: 'login' },
];
