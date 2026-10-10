import { Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SignedInUser, ThemeName } from '@shared/services/auth-state';

@Component({
  selector: 'app-shell-header',
  imports: [RouterLink],
  templateUrl: './shell-header.html',
})
export class ShellHeader {
  readonly isLibraryPage = input.required<boolean>();
  readonly searchQuery = input.required<string>();
  readonly user = input.required<SignedInUser | null>();
  readonly themeOptions = input.required<ThemeName[]>();
  readonly searchQueryChange = output<string>();
  readonly themeChange = output<Event>();
  readonly signOutRequested = output<void>();
}
