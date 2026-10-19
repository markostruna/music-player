import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { PlayerBar } from './shell/player-bar/player-bar';

@Component({
  imports: [RouterOutlet, PlayerBar],
  selector: 'app-root',
  template: '<router-outlet /><app-player-bar />',
})
export class App {}
