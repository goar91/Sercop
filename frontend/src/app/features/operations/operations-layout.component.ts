import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-operations-layout',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './operations-layout.component.html',
  styleUrl: './operations-layout.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationsLayoutComponent {}
