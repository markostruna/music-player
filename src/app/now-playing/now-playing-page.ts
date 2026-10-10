import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { formatTime } from '@shared/utils/format-time';
import { PlayerService } from '@shared/services/player.service';

@Component({
  selector: 'app-now-playing-page',
  imports: [RouterLink],
  templateUrl: './now-playing-page.html',
})
export class NowPlayingPage {
  protected readonly player = inject(PlayerService);
  protected readonly track = this.player.currentTrack;
  protected readonly formatTime = formatTime;
  protected readonly showQueue = signal(false);
  protected readonly year = computed(() =>
    (this.track()?.year ?? 0) > 0 ? this.track()!.year : null,
  );

  protected seek(event: Event): void {
    this.player.seek(Number((event.target as HTMLInputElement).value));
  }

  protected setVolume(event: Event): void {
    this.player.setVolume(Number((event.target as HTMLInputElement).value));
  }
}
