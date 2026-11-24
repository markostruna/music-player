import { GenreSource } from '@shared/services/music-library-api';

export function genreSourceClass(genre: GenreSource): string {
  return genre.fromTags && genre.fromAnalysis
    ? 'genre-source--both'
    : genre.fromTags ? 'genre-source--tags' : 'genre-source--analysis';
}

export function genreSourceLabel(genre: GenreSource): string {
  return genre.fromTags && genre.fromAnalysis
    ? 'ID3 / tags + analyzed'
    : genre.fromTags ? 'ID3 / tags' : 'Analyzed';
}
