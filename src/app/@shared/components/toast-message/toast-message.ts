import { Component, inject } from '@angular/core';
import { ToastService } from '@shared/services/toast-service';

@Component({
  selector: 'app-toast-message',
  template: `
    @if (toast.current(); as message) {
      <div
        class="toast-message"
        [class.toast-message-error]="message.type === 'error'"
        [attr.role]="message.type === 'error' ? 'alert' : 'status'"
        [attr.aria-live]="message.type === 'error' ? 'assertive' : 'polite'"
      >
        <span>{{ message.message }}</span>
        <button type="button" aria-label="Dismiss notification" (click)="toast.dismiss()">
          &times;
        </button>
      </div>
    }
  `,
})
export class ToastMessageComponent {
  readonly toast = inject(ToastService);
}
