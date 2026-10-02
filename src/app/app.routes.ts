import { Routes } from '@angular/router';
import { requireAuth } from './auth.guard';
import { LoginPage } from './login-page';
import { LibraryPage } from './library-page';
import { requireAdmin } from './admin.guard';
import { AdminPage } from './admin-page';
import { AlbumPage } from './album-page';

export const routes: Routes = [
	{ path: '', pathMatch: 'full', redirectTo: 'login' },
	{ path: 'login', component: LoginPage },
	{ path: 'library/album/:trackId', component: AlbumPage, canActivate: [requireAuth] },
	{ path: 'library', component: LibraryPage, canActivate: [requireAuth] },
	{ path: 'admin', component: AdminPage, canActivate: [requireAdmin] },
	{ path: '**', redirectTo: 'login' },
];
