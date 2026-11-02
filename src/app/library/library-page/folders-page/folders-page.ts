import { Component, computed, effect, inject, input, signal } from '@angular/core';
import {
  MusicFolderBrowseEntry,
  MusicFolderContents,
  MusicLibraryApi,
  MusicTrack,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';

interface FolderDisplayFile {
  name: string;
  relativePath: string;
  track: MusicTrack | null;
  type: 'image' | 'music' | 'other';
}

@Component({
  selector: 'app-library-folders-page',
  templateUrl: './folders-page.html',
  styleUrl: './folders-page.scss',
})
export class FoldersPage {
  private readonly library = inject(MusicLibraryApi);
  private readonly player = inject(PlayerService);

  readonly active = input(false);
  readonly refreshTrigger = input(0);
  readonly roots = signal<MusicFolderBrowseEntry[]>([]);
  readonly selectedFolder = signal<MusicFolderBrowseEntry | null>(null);
  readonly contents = signal<MusicFolderContents | null>(null);
  readonly playableTracks = signal<MusicTrack[]>([]);
  readonly isLoading = signal(false);
  readonly errorMessage = signal('');
  readonly tracksErrorMessage = signal('');
  readonly files = computed<FolderDisplayFile[]>(() => {
    const tracksByPath = new Map(this.playableTracks().map((track) => [track.relativePath, track]));
    return (this.contents()?.files ?? []).map((file) => {
      const extension = file.name.split('.').pop()?.toLowerCase() ?? '';
      return {
        ...file,
        track: tracksByPath.get(file.relativePath) ?? null,
        type: ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg', 'tif', 'tiff'].includes(extension)
          ? 'image'
          : ['mp3', 'flac', 'wav'].includes(extension)
            ? 'music'
            : 'other',
      };
    });
  });
  readonly breadcrumbs = computed(() => {
    const selected = this.selectedFolder();
    if (!selected) return [];

    const root = this.roots().find((folder) => folder.sourceRootId === selected.sourceRootId);
    if (!root) return [];

    const path = selected.relativePath.split('/').filter(Boolean);
    return [
      root,
      ...path.map((name, index) => ({
        ...root,
        name,
        relativePath: path.slice(0, index + 1).join('/'),
      })),
    ];
  });

  private loaded = false;
  private lastRefreshTrigger = 0;

  constructor() {
    effect(() => {
      const active = this.active();
      const refreshTrigger = this.refreshTrigger();
      if (!active) return;
      if (!this.loaded || refreshTrigger !== this.lastRefreshTrigger) {
        this.lastRefreshTrigger = refreshTrigger;
        void this.loadFolders();
      }
    });
  }

  async loadFolders(): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      const roots = await this.library.listLibraryFolderRoots();
      this.roots.set(roots);
      this.loaded = true;

      const selected = this.selectedFolder();
      if (!selected) {
        this.contents.set(null);
        return;
      }

      const root = roots.find((folder) => folder.sourceRootId === selected.sourceRootId);
      if (!root) {
        this.selectedFolder.set(null);
        this.contents.set(null);
        return;
      }

      const currentFolder = { ...root, relativePath: selected.relativePath, name: selected.name };
      await this.loadFolderContents(currentFolder);
    } catch {
      this.errorMessage.set('The music folders could not be loaded.');
    } finally {
      this.isLoading.set(false);
    }
  }

  navigateTo(folder: MusicFolderBrowseEntry): void {
    void this.loadFolderContents(folder);
  }

  playTrack(track: MusicTrack): void {
    const queue = this.playableTracks();
    this.player.play(
      queue,
      queue.findIndex((candidate) => candidate.id === track.id),
    );
  }

  private async loadFolderContents(folder: MusicFolderBrowseEntry): Promise<void> {
    this.selectedFolder.set(folder);
    this.contents.set(null);
    this.playableTracks.set([]);
    this.isLoading.set(true);
    this.errorMessage.set('');
    this.tracksErrorMessage.set('');
    try {
      const contents = await this.library.browseLibraryFolder(
        folder.sourceRootId,
        folder.relativePath,
      );
      this.contents.set(contents);
      try {
        this.playableTracks.set(
          await this.library.searchTracks('', {
            sourceRootId: folder.sourceRootId,
            path: folder.relativePath,
          }),
        );
      } catch {
        this.tracksErrorMessage.set('Playable tracks could not be loaded.');
      }
    } catch {
      this.errorMessage.set('The selected music folder could not be opened.');
    } finally {
      this.isLoading.set(false);
    }
  }

  navigateToAllFolders(): void {
    this.selectedFolder.set(null);
    this.contents.set(null);
    this.playableTracks.set([]);
    this.errorMessage.set('');
    this.tracksErrorMessage.set('');
  }
}
