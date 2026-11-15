import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ImportUnscannedAlbum,
  MusicFolderBrowseEntry,
  MusicFolderContents,
  MusicLibraryApi,
  MusicTrack,
  SourceRoot,
  UnscannedAlbumCheck,
} from '@shared/services/music-library-api';
import { PlayerService } from '@shared/services/player.service';
import { AuthState } from '@shared/services/auth-state';
import { LibraryOperationTracker } from '@shared/services/library-operation-tracker';
import { errorMessage as readErrorMessage } from '../../album-actions/error-message';

interface FolderDisplayFile {
  name: string;
  relativePath: string;
  track: MusicTrack | null;
  type: 'image' | 'music' | 'other';
}

@Component({
  selector: 'app-library-folders-page',
  imports: [FormsModule],
  templateUrl: './folders-page.html',
  styleUrl: './folders-page.scss',
})
export class FoldersPage {
  private readonly library = inject(MusicLibraryApi);
  private readonly player = inject(PlayerService);
  private readonly auth = inject(AuthState);
  private readonly operations = inject(LibraryOperationTracker);
  readonly isAdmin = this.auth.isAdmin;

  readonly active = input(false);
  readonly refreshTrigger = input(0);
  readonly roots = signal<MusicFolderBrowseEntry[]>([]);
  readonly scannedRoots = signal<SourceRoot[]>([]);
  readonly unscannedRootIds = signal<Set<number>>(new Set());
  readonly selectedFolder = signal<MusicFolderBrowseEntry | null>(null);
  readonly selectedIsUnscanned = signal(false);
  readonly contents = signal<MusicFolderContents | null>(null);
  readonly playableTracks = signal<MusicTrack[]>([]);
  readonly albumCheck = signal<UnscannedAlbumCheck | null>(null);
  readonly albumErrorMessage = signal('');
  readonly albumStatusMessage = signal('');
  readonly destinationRootId = signal<number | null>(null);
  readonly conflictAction = signal<'' | 'rename' | 'delete'>('');
  readonly cueProcessingChoice = signal<'' | 'split' | 'keep-as-is'>('');
  readonly cueSourceChoice = signal<'' | 'keep' | 'delete'>('');
  readonly existingAlbumNewName = signal('');
  readonly importArtist = signal('');
  readonly albumTitle = signal('');
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
  private handledOperationId = '';

  constructor() {
    effect(() => {
      const active = this.active();
      const refreshTrigger = this.refreshTrigger();
      const operation = this.operations.operation();
      if (
        operation?.state === 'completed'
        && operation.operationType === 'import-scan-metadata'
        && operation.id !== this.handledOperationId
      ) {
        this.handledOperationId = operation.id;
        this.loaded = false;
        if (active) void this.loadFolders();
      }
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
      let allRoots = roots;
      if (this.auth.isAdmin()) {
        const [unscanned, scanned] = await Promise.all([
          this.library.listUnscannedFolders(),
          this.library.listRoots(),
        ]);
        this.scannedRoots.set(scanned.filter((root) => root.isEnabled));
        this.unscannedRootIds.set(new Set(unscanned.map((folder) => folder.id)));
        allRoots = [
          ...roots,
          ...unscanned.map((folder) => ({
            sourceRootId: folder.id,
            name: folder.name,
            relativePath: '',
          })),
        ];
      } else {
        this.scannedRoots.set([]);
        this.unscannedRootIds.set(new Set());
      }
      this.roots.set(allRoots);
      this.loaded = true;

      const selected = this.selectedFolder();
      if (!selected) {
        this.contents.set(null);
        return;
      }

      const root = allRoots.find((folder) => folder.sourceRootId === selected.sourceRootId);
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
    void this.loadFolderContents(folder, this.unscannedRootIds().has(folder.sourceRootId));
  }

  async renameFolder(folder: MusicFolderBrowseEntry): Promise<void> {
    const name = globalThis.prompt('New folder name', folder.name);
    if (name === null || !name.trim()) return;

    try {
      await this.library.renameUnscannedFolder(folder.sourceRootId, folder.relativePath, name.trim());
      this.albumStatusMessage.set('Folder renamed.');
      await this.loadFolders();
    } catch (error) {
      this.errorMessage.set(readErrorMessage(error));
    }
  }

  async renameTrack(file: { name: string; relativePath: string }): Promise<void> {
    const extensionIndex = file.name.lastIndexOf('.');
    const stem = extensionIndex > 0 ? file.name.slice(0, extensionIndex) : file.name;
    const name = globalThis.prompt('New track filename', stem);
    if (name === null || !name.trim()) return;

    try {
      await this.library.renameUnscannedTrack(
        this.selectedFolder()!.sourceRootId,
        file.relativePath,
        name.trim(),
      );
      await this.loadFolders();
    } catch (error) {
      this.errorMessage.set(readErrorMessage(error));
    }
  }

  async checkAlbum(): Promise<void> {
    const selected = this.selectedFolder();
    if (!selected) return;
    this.albumErrorMessage.set('');
    this.albumStatusMessage.set('');
    try {
      if (this.destinationRootId() === null && this.scannedRoots().length > 0) {
        this.destinationRootId.set(this.scannedRoots()[0].id);
      }
      const destinationRootId = this.destinationRootId();
      if (destinationRootId === null) {
        this.albumErrorMessage.set('Add a scanned music folder before checking albums.');
        return;
      }
      const result = await this.library.checkUnscannedAlbum(
        selected.sourceRootId,
        selected.relativePath,
        this.importArtist(),
        this.albumTitle(),
        destinationRootId,
      );
      this.albumCheck.set(result);
      this.conflictAction.set('');
      this.cueProcessingChoice.set('');
      this.cueSourceChoice.set('');
      this.existingAlbumNewName.set(`${this.albumTitle()} (existing)`);
    } catch (error) {
      this.albumErrorMessage.set(readErrorMessage(error));
    }
  }

  async importAlbum(): Promise<void> {
    const selected = this.selectedFolder();
    const destinationRootId = this.destinationRootId();
    const check = this.albumCheck();
    if (!selected || destinationRootId === null || !check) return;
    if ((check.existingAlbums.length > 0 || check.destinationFolderExists) && !this.conflictAction()) {
      this.albumErrorMessage.set('Choose how to handle the existing album before importing.');
      return;
    }
    if (
      check.hasCueFile
      && (!this.cueProcessingChoice()
        || (this.cueProcessingChoice() === 'split' && !this.cueSourceChoice()))
    ) {
      this.albumErrorMessage.set('Choose how to handle the CUE file before importing.');
      return;
    }

    const request: ImportUnscannedAlbum = {
      relativePath: selected.relativePath,
      destinationRootId,
      artist: this.importArtist().trim(),
      album: this.albumTitle().trim(),
      conflictAction: this.conflictAction(),
      existingAlbumNewName: this.existingAlbumNewName().trim(),
      splitCue: this.cueProcessingChoice() === 'split',
      keepCueSources: this.cueSourceChoice() === 'keep',
    };
    try {
      await this.operations.startAlbumImport(selected.sourceRootId, request);
      this.albumStatusMessage.set('Album moved. Scanning and metadata refresh have started.');
      this.albumErrorMessage.set('');
    } catch (error) {
      this.albumErrorMessage.set(readErrorMessage(error));
    }
  }

  playTrack(track: MusicTrack): void {
    this.player.play([track], 0);
  }

  private async loadFolderContents(
    folder: MusicFolderBrowseEntry,
    isUnscanned = this.unscannedRootIds().has(folder.sourceRootId),
  ): Promise<void> {
    this.selectedFolder.set(folder);
    this.selectedIsUnscanned.set(isUnscanned);
    this.contents.set(null);
    this.playableTracks.set([]);
    this.albumCheck.set(null);
    this.cueProcessingChoice.set('');
    this.cueSourceChoice.set('');
    this.albumErrorMessage.set('');
    this.albumStatusMessage.set('');
    this.importArtist.set(this.getArtistSuggestion(folder));
    this.albumTitle.set(folder.name);
    this.isLoading.set(true);
    this.errorMessage.set('');
    this.tracksErrorMessage.set('');
    try {
      const contents = isUnscanned
        ? await this.library.browseUnscannedFolder(folder.sourceRootId, folder.relativePath)
        : await this.library.browseLibraryFolder(folder.sourceRootId, folder.relativePath);
      this.contents.set(contents);
      if (!isUnscanned) {
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
      }
    } catch {
      this.errorMessage.set('The selected music folder could not be opened.');
    } finally {
      this.isLoading.set(false);
    }
  }

  navigateToAllFolders(): void {
    this.selectedFolder.set(null);
    this.selectedIsUnscanned.set(false);
    this.contents.set(null);
    this.playableTracks.set([]);
    this.errorMessage.set('');
    this.tracksErrorMessage.set('');
  }

  private getArtistSuggestion(folder: MusicFolderBrowseEntry): string {
    const path = folder.relativePath.split('/').filter(Boolean);
    if (path.length > 1) return path[path.length - 2];
    return '';
  }
}
