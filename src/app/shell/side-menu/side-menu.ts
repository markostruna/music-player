import { Component, input, output } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { SignedInUser } from '@shared/services/auth-state';

@Component({
  selector: 'app-side-menu',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './side-menu.html',
})
export class SideMenu {
  readonly isLibraryPage = input.required<boolean>();
  readonly activeSection = input.required<string>();
  readonly user = input.required<SignedInUser | null>();
  readonly activeSectionChange = output<string>();
  readonly signOutRequested = output<void>();
}
