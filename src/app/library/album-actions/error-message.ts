import { HttpErrorResponse } from '@angular/common/http';

export function errorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error;
    if (typeof body === 'string' && body) return body;
    if (body && typeof body === 'object' && 'detail' in body) return String(body.detail);
    if (error.status === 403) return 'You do not have permission to do that.';
  }
  return error instanceof Error ? error.message : 'Something went wrong. Please try again.';
}
