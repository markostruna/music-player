import { Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { GenreSource, ListeningStatus, MusicLibraryApi, MusicTrack } from '@shared/services/music-library-api';
import { TrackGrid } from '@shared/components/track-grid/track-grid';
import { genreSourceClass, genreSourceLabel } from '@shared/utils/genre-source';

@Component({
  selector: 'app-search-page',
  imports: [FormsModule, TrackGrid],
  templateUrl: './search-page.html',
  styleUrl: './search-page.scss',
})
export class SearchPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly library = inject(MusicLibraryApi);
  private request = 0;
  private activeFilters: { query: string; genre: string; status: ListeningStatus } = {
    query: '', genre: '', status: 'all',
  };
  protected query = '';
  protected genre = '';
  protected status: ListeningStatus = 'all';
  protected readonly tracks = signal<MusicTrack[]>([]);
  protected readonly genres = signal<GenreSource[]>([]);
  protected readonly genresError = signal('');
  protected readonly genreSourceClass = genreSourceClass;
  protected readonly genreSourceLabel = genreSourceLabel;
  protected readonly loading = signal(false);
  protected readonly error = signal('');
  protected readonly page = signal(1);
  protected readonly total = signal(0);
  protected readonly pageSize = 50;

  constructor() {
    this.route.queryParamMap.pipe(takeUntilDestroyed(inject(DestroyRef))).subscribe((params) => {
      this.query = params.get('query') ?? '';
      this.genre = params.get('genre') ?? '';
      this.matchGenreOption();
      const status = params.get('status');
      this.status = status === 'recent' || status === 'progress' || status === 'unplayed' ? status : 'all';
      this.activeFilters = { query: this.query, genre: this.genre, status: this.status };
      void this.load(1);
    });
    void this.loadGenres();
  }

  private async loadGenres(): Promise<void> {
    try {
      this.genres.set(await this.library.listGenreSources());
      this.matchGenreOption();
      this.genresError.set('');
    } catch {
      this.genresError.set('Genres could not be loaded. Check the music server connection.');
    }
  }

  private matchGenreOption(): void {
    const matched = this.genres().find((item) => item.name.toLowerCase() === this.genre.toLowerCase());
    if (matched) this.genre = matched.name;
  }

  protected selectedGenre(): GenreSource | undefined {
    return this.genres().find((item) => item.name === this.genre);
  }

  protected search(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { query: this.query || null, genre: this.genre || null, status: this.status === 'all' ? null : this.status },
    });
  }

  protected async load(page: number): Promise<void> {
    const request = ++this.request;
    this.loading.set(true);
    this.error.set('');
    try {
      const { query, genre, status } = this.activeFilters;
      const result = await this.library.browseListeningTracks(query, genre, status, page, this.pageSize);
      if (request !== this.request) return;
      this.tracks.set(result.items);
      this.total.set(result.totalCount);
      this.page.set(result.page);
    } catch {
      if (request !== this.request) return;
      this.tracks.set([]);
      this.error.set('Tracks could not be loaded. Check the music server connection.');
    } finally {
      if (request === this.request) this.loading.set(false);
    }
  }
}
