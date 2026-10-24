import { Routes } from '@angular/router';

export const libraryRoutes: Routes = [
  {
    path: 'library/album/:trackId',
    loadComponent: () => import('./album-page/album-page').then((module) => module.AlbumPage),
  },
  {
    path: 'library/artist/:name',
    loadComponent: () => import('./artist-page/artist-page').then((module) => module.ArtistPage),
  },
  {
    path: 'library',
    loadComponent: () => import('./library-page/library-page').then((module) => module.LibraryPage),
  },
];
