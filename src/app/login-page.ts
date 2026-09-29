import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthState } from './auth-state';

@Component({
  selector: 'app-login-page',
  imports: [FormsModule, RouterLink],
  templateUrl: './login-page.html',
})
export class LoginPage {
  private readonly auth = inject(AuthState);
  private readonly router = inject(Router);

  email = '';
  password = '';
  readonly isSubmitting = signal(false);
  readonly errorMessage = signal('');

  async signIn(): Promise<void> {
    if (this.isSubmitting()) return;
    this.isSubmitting.set(true);
    this.errorMessage.set('');
    try {
      await this.auth.signIn(this.email, this.password);
      await this.router.navigateByUrl('/library');
    } catch {
      this.errorMessage.set('That email and password combination was not recognized.');
    } finally {
      this.isSubmitting.set(false);
    }
  }
}
