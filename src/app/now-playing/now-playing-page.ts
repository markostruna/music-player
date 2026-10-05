import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PlayerService } from '@shared/services/player.service';

@Component({
  selector: 'app-now-playing-page',
  imports: [RouterLink],
  templateUrl: './now-playing-page.html',
})
export class NowPlayingPage {
  protected readonly player = inject(PlayerService);
  protected readonly track = this.player.currentTrack;
  protected readonly year = computed(() =>
    (this.track()?.year ?? 0) > 0 ? this.track()!.year : null,
  );
}
