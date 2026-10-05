import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class MusicShellState {
  readonly activeSection = signal('Your library');
}
