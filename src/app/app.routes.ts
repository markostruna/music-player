import { Routes } from '@angular/router';
import { requireAdmin } from './core/auth/admin.guard';
import { requireAuth } from './core/auth/auth.guard';
import { AdminPage } from './features/admin/admin-page';
import { LoginPage } from './features/auth/login/login-page';
import { LibraryPage } from './features/library/library-page';

export const routes: Routes = [
	{ path: '', pathMatch: 'full', redirectTo: 'login' },
	{ path: 'login', component: LoginPage },
	{ path: 'library', component: LibraryPage, canActivate: [requireAuth] },
	{ path: 'admin', component: AdminPage, canActivate: [requireAdmin] },
	{ path: '**', redirectTo: 'login' },
];
