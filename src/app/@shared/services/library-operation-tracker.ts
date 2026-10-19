import { computed, inject, Injectable, signal } from '@angular/core';
import { LibraryStore } from './library-store';
import { LibraryOperationStatus, MusicLibraryApi } from './music-library-api';

@Injectable({ providedIn: 'root' })
export class LibraryOperationTracker {
  private readonly api = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  private pollTimeout: ReturnType<typeof setTimeout> | null = null;
  private hideTimeout: ReturnType<typeof setTimeout> | null = null;
  private readonly initialRestore: Promise<void>;

  readonly operation = signal<LibraryOperationStatus | null>(null);
  readonly isRunning = computed(() => {
    const state = this.operation()?.state;
    return state === 'queued' || state === 'running';
  });
  readonly pollingError = signal(false);

  constructor() {
    this.initialRestore = this.restoreActiveOperation();
  }

  async startScan(rootId: number): Promise<void> {
    await this.start(rootId, 'scan');
  }

  async startMetadataRefresh(rootId: number): Promise<void> {
    await this.start(rootId, 'metadata-refresh');
  }

  private async start(rootId: number, type: 'scan' | 'metadata-refresh'): Promise<void> {
    await this.initialRestore;
    if (this.isRunning()) {
      throw new Error('Another library operation is already running.');
    }

    this.clearTimers();
    this.operation.set(null);
    this.pollingError.set(false);
    const operation =
      type === 'scan'
        ? await this.api.scanRoot(rootId)
        : await this.api.refreshRootMetadata(rootId);
    this.operation.set(operation);
    this.schedulePoll(700);
  }

  private async restoreActiveOperation(): Promise<void> {
    try {
      const operation = await this.api.getActiveLibraryOperation();
      if (operation && (operation.state === 'queued' || operation.state === 'running')) {
        this.operation.set(operation);
        this.schedulePoll(0);
      }
    } catch {
      this.pollingError.set(true);
    }
  }

  private async poll(): Promise<void> {
    const current = this.operation();
    if (!current || (current.state !== 'queued' && current.state !== 'running')) {
      return;
    }

    try {
      const updated = await this.api.getLibraryOperation(current.id);
      this.operation.set(updated);
      this.pollingError.set(false);
      if (updated.state === 'completed') {
        void this.store.refresh();
        this.scheduleHide(updated.id, 8000);
        return;
      }
      if (updated.state === 'failed') {
        this.scheduleHide(updated.id, 15000);
        return;
      }
      this.schedulePoll(1000);
    } catch {
      this.pollingError.set(true);
      this.schedulePoll(3000);
    }
  }

  private schedulePoll(delay: number): void {
    if (this.pollTimeout !== null) clearTimeout(this.pollTimeout);
    this.pollTimeout = setTimeout(() => {
      this.pollTimeout = null;
      void this.poll();
    }, delay);
  }

  private scheduleHide(operationId: string, delay: number): void {
    if (this.hideTimeout !== null) clearTimeout(this.hideTimeout);
    this.hideTimeout = setTimeout(() => {
      if (this.operation()?.id === operationId) {
        this.operation.set(null);
      }
      this.hideTimeout = null;
    }, delay);
  }

  private clearTimers(): void {
    if (this.pollTimeout !== null) clearTimeout(this.pollTimeout);
    if (this.hideTimeout !== null) clearTimeout(this.hideTimeout);
    this.pollTimeout = null;
    this.hideTimeout = null;
  }
}
