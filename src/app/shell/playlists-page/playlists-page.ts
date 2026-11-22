import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { BUILT_IN_PLAYLISTS, PlaylistService } from '@shared/services/playlist.service';

@Component({
  selector: 'app-playlists-page',
  imports: [FormsModule, RouterLink],
  templateUrl: './playlists-page.html',
  styleUrl: '../settings-page/settings-page.scss',
})
export class PlaylistsPage implements OnInit {
  private readonly service = inject(PlaylistService);
  private readonly router = inject(Router);
  readonly builtIns = BUILT_IN_PLAYLISTS;
  readonly playlists = this.service.playlists;
  readonly error = signal('');
  readonly busy = signal(false);
  name = '';

  ngOnInit(): void {
    void this.service.load().catch(() => this.error.set('Playlists could not be loaded.'));
  }

  async create(): Promise<void> {
    if (!this.name.trim() || this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const playlist = await this.service.create(this.name.trim());
      await this.router.navigate(['/playlists', playlist.id]);
    } catch {
      this.error.set('Playlist could not be created.');
    } finally {
      this.busy.set(false);
    }
  }
}
