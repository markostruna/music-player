import { Routes } from '@angular/router';

export const nowPlayingRoutes: Routes = [
  {
    path: 'now-playing',
    loadComponent: () =>
      import('./now-playing-page').then((module) => module.NowPlayingPage),
  },
];
