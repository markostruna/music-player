import { Component, computed, ElementRef, inject, OnInit, signal, ViewChild } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MusicLibraryApi, MusicTrack } from './music-library-api';

@Component({
  selector: 'app-album-page',
  imports: [RouterLink],
  templateUrl: './album-page.html',
})
export class AlbumPage implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly library = inject(MusicLibraryApi);

  @ViewChild('audioPlayer') private audioPlayer?: ElementRef<HTMLAudioElement>;

  readonly tracks = signal<MusicTrack[]>([]);
  readonly currentTrack = signal<MusicTrack | null>(null);
  readonly isLoading = signal(true);
  readonly errorMessage = signal('');
  readonly playbackError = signal('');
  readonly isPlaying = signal(false);
  readonly album = computed(() => {
    const trackId = Number(this.route.snapshot.paramMap.get('trackId'));
    const selectedTrack = this.tracks().find((track) => track.id === trackId);
    if (!selectedTrack) return null;

    const artist = selectedTrack.albumArtist.trim() || selectedTrack.artist;
    const albumTracks = this.tracks()
      .filter((track) =>
        track.album.toLowerCase() === selectedTrack.album.toLowerCase()
        && (track.albumArtist.trim() || track.artist).toLowerCase() === artist.toLowerCase())
      .sort((first, second) =>
        first.discNumber - second.discNumber
        || first.trackNumber - second.trackNumber
        || first.title.localeCompare(second.title));

    return {
      title: selectedTrack.album,
      artist,
      coverUrl: selectedTrack.coverUrl,
      releaseYear: albumTracks.find((track) => track.year > 0)?.year,
      tracks: albumTracks,
    };
  });

  ngOnInit(): void {
    void this.loadAlbum();
  }

  async loadAlbum(): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      this.tracks.set(await this.library.listTracks());
      if (!this.album()) this.errorMessage.set('This album could not be found in your library.');
    } catch {
      this.errorMessage.set('The album could not be loaded. Check the music server connection.');
    } finally {
      this.isLoading.set(false);
    }
  }

  playTrack(track: MusicTrack): void {
    this.currentTrack.set(track);
    this.playbackError.set('');
    const audio = this.audioPlayer?.nativeElement;
    if (!audio) return;
    audio.pause();
    audio.src = track.streamUrl;
    audio.load();
    void audio.play().then(() => this.isPlaying.set(true)).catch(() => {
      this.isPlaying.set(false);
      this.playbackError.set('This track could not be played in this browser.');
    });
  }

  playNextTrack(): void {
    const albumTracks = this.album()?.tracks ?? [];
    const nextIndex = albumTracks.findIndex((track) => track.id === this.currentTrack()?.id) + 1;
    const nextTrack = albumTracks[nextIndex];
    if (nextTrack) this.playTrack(nextTrack);
    else this.isPlaying.set(false);
  }

  formatTime(seconds: number): string {
    if (!Number.isFinite(seconds)) return '0:00';
    return `${Math.floor(seconds / 60)}:${Math.floor(seconds % 60).toString().padStart(2, '0')}`;
  }
}
