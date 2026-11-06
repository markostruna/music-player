import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { PlayerBar } from './shell/player-bar/player-bar';
import { DEMO_MODE } from '@shared/utils/api-path';

@Component({
  imports: [RouterOutlet, PlayerBar],
  selector: 'app-root',
  template: `
    @if (demoMode) {
      <div class="demo-badge">Demo library · changes reset on reload</div>
    }
    <router-outlet /><app-player-bar />
  `,
  styles: `
    .demo-badge {
      position: fixed;
      bottom: 100px;
      right: 12px;
      z-index: 1000;
      padding: 6px 10px;
      border-radius: 4px;
      background: #222;
      color: #fff;
      font-size: 12px;
      pointer-events: none;
    }
  `,
})
export class App {
  readonly demoMode = DEMO_MODE;
}
