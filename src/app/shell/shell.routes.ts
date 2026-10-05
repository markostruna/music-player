import { Routes } from '@angular/router';
import { requireAuth } from '@shared/guards/auth.guard';
import { libraryRoutes } from '../library/library.routes';
import { nowPlayingRoutes } from '../now-playing/now-playing.routes';
import { MusicShell } from './music-shell/music-shell';

export const shellRoutes: Routes = [
  {
    path: '',
    component: MusicShell,
    canActivateChild: [requireAuth],
    children: [...libraryRoutes, ...nowPlayingRoutes],
  },
];
