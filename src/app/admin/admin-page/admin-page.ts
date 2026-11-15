import { Component, effect, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  LibraryStore,
  ManagedUser,
  MusicLibraryApi,
  SourceRoot,
  UnscannedFolder,
} from '@shared/services';
import { UserRole } from '@shared/services/auth-state';
import { LibraryOperationTracker } from '@shared/services/library-operation-tracker';

@Component({
  selector: 'app-admin-page',
  imports: [FormsModule],
  templateUrl: './admin-page.html',
})
export class AdminPage implements OnInit {
  private readonly api = inject(MusicLibraryApi);
  private readonly store = inject(LibraryStore);
  readonly operations = inject(LibraryOperationTracker);
  private readonly handledOperations = new Set<string>();

  readonly roots = signal<SourceRoot[]>([]);
  readonly unscannedFolders = signal<UnscannedFolder[]>([]);
  readonly users = signal<ManagedUser[]>([]);
  readonly isLoading = signal(true);
  readonly errorMessage = signal('');
  readonly statusMessage = signal('');
  readonly roles: UserRole[] = ['Admin', 'Guest'];

  rootName = '';
  rootPath = '';
  unscannedFolderName = '';
  unscannedFolderPath = '';
  newEmail = '';
  temporaryPassword = '';
  newRole: UserRole = 'Guest';
  resetPasswords: Record<number, string> = {};

  constructor() {
    effect(() => {
      const operation = this.operations.operation();
      if (operation?.operationType === 'database-cleanup' &&
          (operation.state === 'completed' || operation.state === 'failed') &&
          !this.handledOperations.has(operation.id)) {
        if (operation.state === 'completed') {
          this.statusMessage.set(operation.message);
        } else {
          this.errorMessage.set(operation.message);
          this.handledOperations.add(operation.id);
        }
      }
      if (operation?.state === 'completed' && !this.handledOperations.has(operation.id)) {
        this.handledOperations.add(operation.id);
        void this.reload();
      }
    });
  }

  ngOnInit(): void {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set('');
    try {
      const [roots, unscannedFolders, users] = await Promise.all([
        this.api.listRoots(),
        this.api.listUnscannedFolders(),
        this.api.listUsers(),
      ]);
      this.roots.set(roots.filter((root) => root.isEnabled));
      this.unscannedFolders.set(unscannedFolders);
      this.users.set(users);
    } catch {
      this.errorMessage.set('Administrator data could not be loaded.');
    } finally {
      this.isLoading.set(false);
    }
  }

  async addUnscannedFolder(): Promise<void> {
    try {
      await this.api.addUnscannedFolder(this.unscannedFolderName, this.unscannedFolderPath);
      this.unscannedFolderName = '';
      this.unscannedFolderPath = '';
      this.statusMessage.set('Unscanned folder added. It will not be included in library scans.');
      await this.reload();
    } catch {
      this.errorMessage.set(
        'The unscanned folder could not be added. Check that the server path exists and does not overlap a configured music folder.',
      );
    }
  }

  async removeUnscannedFolder(folder: UnscannedFolder): Promise<void> {
    const confirmed = globalThis.confirm(
      `Remove ${folder.name} from the unscanned-folder list? Its files will remain untouched.`,
    );
    if (!confirmed) return;

    try {
      await this.api.removeUnscannedFolder(folder.id);
      this.statusMessage.set(`${folder.name} was removed from the list. Its files were left untouched.`);
      await this.reload();
    } catch {
      this.errorMessage.set('The unscanned folder could not be removed.');
    }
  }

  async addRoot(): Promise<void> {
    try {
      const root = await this.api.addRoot(this.rootName, this.rootPath);
      this.rootName = '';
      this.rootPath = '';
      await this.reload();
      try {
        await this.operations.startScan(root.id);
        this.statusMessage.set('Music folder added. Its initial scan has started.');
      } catch {
        this.statusMessage.set(
          'Music folder added, but its initial scan could not start. Use Scan to try again.',
        );
      }
    } catch {
      this.errorMessage.set(
        'That folder could not be added. Check that it exists and is not already configured.',
      );
    }
  }

  async scanRoot(root: SourceRoot): Promise<void> {
    this.errorMessage.set('');
    this.statusMessage.set('');
    try {
      await this.operations.startScan(root.id);
    } catch {
      this.errorMessage.set(`The scan of ${root.name} could not be completed.`);
    }
  }

  async cleanDatabase(): Promise<void> {
    if (!globalThis.confirm(
      'Remove missing tracks and folders from the database? Music files will not be deleted. Unavailable sources will be skipped.',
    )) return;

    this.errorMessage.set('');
    this.statusMessage.set('');
    try {
      await this.operations.startDatabaseCleanup();
    } catch {
      this.errorMessage.set('Database cleanup could not start. Check whether another library operation is running.');
    }
  }

  async refreshRootMetadata(root: SourceRoot): Promise<void> {
    this.errorMessage.set('');
    this.statusMessage.set('');
    try {
      await this.operations.startMetadataRefresh(root.id);
    } catch {
      this.errorMessage.set(`Metadata for ${root.name} could not be refreshed.`);
    }
  }

  async removeRoot(root: SourceRoot): Promise<void> {
    const confirmed = globalThis.confirm(
      `Remove ${root.name} from the library? Its catalogued tracks and folders will be removed, but music files on disk will stay untouched.`,
    );
    if (!confirmed) return;

    try {
      await this.api.removeRoot(root.id);
      this.store.invalidate();
      this.statusMessage.set(
        `${root.name} was removed from the library. Music files were left untouched.`,
      );
      await this.reload();
    } catch {
      this.errorMessage.set(
        'The music source could not be removed. Refresh the page and try again.',
      );
    }
  }

  async createUser(): Promise<void> {
    try {
      await this.api.createUser(this.newEmail, this.temporaryPassword, this.newRole);
      this.newEmail = '';
      this.temporaryPassword = '';
      this.newRole = 'Guest';
      this.statusMessage.set('Account created. Share its temporary password securely.');
      await this.reload();
    } catch {
      this.errorMessage.set(
        'The account could not be created. Check the email, password length, and whether it already exists.',
      );
    }
  }

  async changeRole(user: ManagedUser, event: Event): Promise<void> {
    const role = (event.target as HTMLSelectElement).value as UserRole;
    try {
      await this.api.setUserRole(user.id, role);
      this.statusMessage.set(`${user.email} is now ${role}.`);
      await this.reload();
    } catch {
      this.errorMessage.set(
        'The role could not be changed. The last active administrator must remain an administrator.',
      );
    }
  }

  async setEnabled(user: ManagedUser, event: Event): Promise<void> {
    const isEnabled = (event.target as HTMLInputElement).checked;
    try {
      await this.api.setUserEnabled(user.id, isEnabled);
      this.statusMessage.set(isEnabled ? 'Account enabled.' : 'Account disabled.');
      await this.reload();
    } catch {
      this.errorMessage.set(
        'The account could not be disabled. The last active administrator must remain enabled.',
      );
    }
  }

  async resetPassword(user: ManagedUser): Promise<void> {
    const password = this.resetPasswords[user.id] ?? '';
    try {
      await this.api.resetPassword(user.id, password);
      this.resetPasswords[user.id] = '';
      this.statusMessage.set(`Password reset for ${user.email}.`);
    } catch {
      this.errorMessage.set('Password reset failed. Use at least 12 characters.');
    }
  }
}
