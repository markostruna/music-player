import { Injectable, signal } from '@angular/core';

export type ToastType = 'success' | 'error';

export interface ToastMessage {
  message: string;
  type: ToastType;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly current = signal<ToastMessage | null>(null);
  private timeout: ReturnType<typeof setTimeout> | null = null;

  show(message: string, type: ToastType): void {
    this.clearTimeout();
    this.current.set({ message, type });
    this.timeout = setTimeout(() => this.dismiss(), type === 'error' ? 8000 : 5000);
  }

  dismiss(): void {
    this.clearTimeout();
    this.current.set(null);
  }

  private clearTimeout(): void {
    if (this.timeout !== null) {
      clearTimeout(this.timeout);
      this.timeout = null;
    }
  }
}
