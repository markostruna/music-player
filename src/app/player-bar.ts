import { DOCUMENT } from '@angular/common';
import { Component, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { formatTime } from './format-time';
import { PlayerService } from './player.service';

@Component({
  selector: 'app-player-bar',
  imports: [RouterLink],
  templateUrl: './player-bar.html',
})
export class PlayerBar {
  protected readonly player = inject(PlayerService);
  protected readonly formatTime = formatTime;
  protected readonly showQueue = signal(false);
  private readonly document = inject(DOCUMENT);

  constructor() {
    effect(() => {
      const hasTrack = this.player.currentTrack() !== null;
      this.document.body.classList.toggle('has-player', hasTrack);
      if (!hasTrack) this.showQueue.set(false);
    });
  }

  protected seek(event: Event): void {
    this.player.seek(Number((event.target as HTMLInputElement).value));
  }

  protected setVolume(event: Event): void {
    this.player.setVolume(Number((event.target as HTMLInputElement).value));
  }
}
