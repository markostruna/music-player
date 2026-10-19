import { Component, effect, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LibraryStore, ManagedUser, MusicLibraryApi, SourceRoot } from '@shared/services';
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
  readonly users = signal<ManagedUser[]>([]);
  readonly isLoading = signal(true);
  readonly errorMessage = signal('');
  readonly statusMessage = signal('');
  readonly roles: UserRole[] = ['Admin', 'Guest'];

  rootName = '';
  rootPath = '';
  newEmail = '';
  temporaryPassword = '';
  newRole: UserRole = 'Guest';
  resetPasswords: Record<number, string> = {};

  constructor() {
    effect(() => {
      const operation = this.operations.operation();
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
      const [roots, users] = await Promise.all([this.api.listRoots(), this.api.listUsers()]);
      this.roots.set(roots);
      this.users.set(users);
    } catch {
      this.errorMessage.set('Administrator data could not be loaded.');
    } finally {
      this.isLoading.set(false);
    }
  }

  async addRoot(): Promise<void> {
    try {
      await this.api.addRoot(this.rootName, this.rootPath);
      this.rootName = '';
      this.rootPath = '';
      this.statusMessage.set('Music folder added. Start a scan to import its tracks.');
      await this.reload();
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
