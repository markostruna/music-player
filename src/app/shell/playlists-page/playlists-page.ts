import { Component } from '@angular/core';

interface Playlist {
  name: string;
  description: string;
  swatch: string;
}

@Component({
  selector: 'app-playlists-page',
  templateUrl: './playlists-page.html',
})
export class PlaylistsPage {
  readonly playlists: Playlist[] = [
    {
      name: 'Late night drive',
      description: 'A soundtrack for the long way home.',
      swatch: 'playlist-swatch-lime',
    },
    {
      name: 'Sunday slowdown',
      description: 'Take it easy and let the day unfold.',
      swatch: 'playlist-swatch-peach',
    },
    {
      name: 'Soft focus',
      description: 'A little space to settle into the moment.',
      swatch: 'playlist-swatch-blue',
    },
  ];
}
