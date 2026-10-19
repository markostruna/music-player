import { Component, input, output } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-side-menu',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './side-menu.html',
})
export class SideMenu {
  readonly isLibraryPage = input.required<boolean>();
  readonly activeSection = input.required<string>();
  readonly activeSectionChange = output<string>();
}
