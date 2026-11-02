import { TestBed } from '@angular/core/testing';
import { LibraryStore, LIBRARY_PAGE_SIZE } from './library-store';
import { MusicLibraryApi } from './music-library-api';

describe('LibraryStore', () => {
  it('reuses cached album pages for the same query until invalidated', async () => {
    let albumPageCalls = 0;
    TestBed.configureTestingModule({
      providers: [
        LibraryStore,
        {
          provide: MusicLibraryApi,
          useValue: {
            listAlbumPage: async (_query: string, page: number) => {
              albumPageCalls++;
              return { items: [], totalCount: LIBRARY_PAGE_SIZE * 4, page };
            },
          },
        },
      ],
    });

    const store = TestBed.inject(LibraryStore);
    await store.loadCollectionPage('albums', '', 1, LIBRARY_PAGE_SIZE);
    await store.loadCollectionPage('albums', '', 2, LIBRARY_PAGE_SIZE, true);

    expect(store.hasCachedCollectionPage('albums', '')).toBe(true);
    expect(store.hasCachedCollectionPage('albums', 'different search')).toBe(false);
    expect(store.currentPages().albums).toBe(2);
    expect(albumPageCalls).toBe(2);

    store.invalidate();
    expect(store.hasCachedCollectionPage('albums', '')).toBe(false);
  });
});
