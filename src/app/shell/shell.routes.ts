import { Routes } from '@angular/router';
import { requireAuth } from '@shared/guards/auth.guard';
import { adminRoutes } from '../admin/admin.routes';
import { libraryRoutes } from '../library/library.routes';
import { nowPlayingRoutes } from '../now-playing/now-playing.routes';
import { MusicShell } from './music-shell/music-shell';
import { PlaylistsPage } from './playlists-page/playlists-page';
import { SettingsPage } from './settings-page/settings-page';

export const shellRoutes: Routes = [
  {
    path: '',
    component: MusicShell,
    canActivateChild: [requireAuth],
    children: [
      ...libraryRoutes,
      ...nowPlayingRoutes,
      ...adminRoutes,
      { path: 'playlists', component: PlaylistsPage },
      { path: 'settings', component: SettingsPage },
    ],
  },
];
