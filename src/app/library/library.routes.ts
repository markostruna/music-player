import { Routes } from '@angular/router';
import { AlbumPage } from './album-page/album-page';
import { ArtistPage } from './artist-page/artist-page';
import { LibraryPage } from './library-page/library-page';

export const libraryRoutes: Routes = [
  { path: 'library/album/:trackId', component: AlbumPage },
  { path: 'library/artist/:name', component: ArtistPage },
  { path: 'library', component: LibraryPage },
];
