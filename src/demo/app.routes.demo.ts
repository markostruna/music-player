import { Routes } from '@angular/router';
import { loginRoutes } from '../app/login/login.routes';
import { shellRoutes } from '../app/shell/shell.routes';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'home' },
  ...loginRoutes,
  ...shellRoutes,
  { path: '**', redirectTo: 'home' },
];
