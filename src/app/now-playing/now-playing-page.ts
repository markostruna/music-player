import {
  Component,
  ElementRef,
  HostListener,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { formatTime } from '@shared/utils/format-time';
import { PlayerService } from '@shared/services/player.service';
import { MusicLibraryApi } from '@shared/services/music-library-api';

@Component({
  selector: 'app-now-playing-page',
  imports: [RouterLink],
  templateUrl: './now-playing-page.html',
})
export class NowPlayingPage {
  protected readonly player = inject(PlayerService);
  private readonly libraryApi = inject(MusicLibraryApi);
  protected readonly track = this.player.currentTrack;
  protected readonly formatTime = formatTime;
  protected readonly artistImageUrl = signal('');
  protected readonly artistImageError = signal('');
  protected readonly queueHeadingStuck = signal(false);
  private readonly contentScroll = viewChild<ElementRef<HTMLElement>>('contentScroll');
  private readonly queueHeading = viewChild<ElementRef<HTMLElement>>('queueHeading');
  private readonly queueList = viewChild<ElementRef<HTMLOListElement>>('queueList');
  private artistImageRequestId = 0;
  protected readonly artistName = computed(() => {
    const track = this.track();
    return track?.albumArtist.trim() || track?.artist.trim() || '';
  });
  protected readonly year = computed(() =>
    (this.track()?.year ?? 0) > 0 ? this.track()!.year : null,
  );

  constructor() {
    afterRenderEffect(() => {
      const currentIndex = this.player.currentIndex();
      this.player.queue();
      this.updateQueueHeadingStuck();
      const list = this.queueList()?.nativeElement;
      if (currentIndex < 0 || !list) return;
      list
        .querySelector<HTMLElement>('[aria-current="true"]')
        ?.scrollIntoView({ block: 'nearest' });
    });

    effect(() => {
      const requestId = ++this.artistImageRequestId;
      const artistName = this.artistName();
      this.artistImageUrl.set('');
      this.artistImageError.set('');
      if (!artistName) return;

      void this.libraryApi
        .getArtistMetadata(artistName)
        .then((metadata) => {
          if (requestId === this.artistImageRequestId) {
            this.artistImageUrl.set(metadata.imageUrl ?? '');
          }
        })
        .catch(() => {
          if (requestId === this.artistImageRequestId) {
            this.artistImageError.set('Artist picture could not be loaded; showing album artwork.');
          }
        });
    });
  }

  @HostListener('window:scroll')
  protected updateQueueHeadingStuck(): void {
    const heading = this.queueHeading()?.nativeElement;
    const container = this.contentScroll()?.nativeElement;
    if (!heading || !container) return;

    const containerScrolls = container.scrollHeight > container.clientHeight + 1;
    const stickyTop = containerScrolls ? container.getBoundingClientRect().top : 0;
    this.queueHeadingStuck.set(heading.getBoundingClientRect().top <= stickyTop + 1);
  }

  protected changePlaybackMode(value: string): void {
    if (value === 'normal' || value === 'shuffle' || value === 'smart')
      this.player.setPlaybackMode(value);
  }
}
