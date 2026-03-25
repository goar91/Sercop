import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShellComponent {
  private readonly auth = inject(AuthService);

  protected readonly user = this.auth.currentUser;
  protected readonly showManagement = computed(() => this.auth.hasAnyRole('admin', 'gerencia'));
  protected readonly showOperations = computed(() => this.auth.hasAnyRole('admin'));
  protected readonly showAutomation = computed(() => this.auth.hasAnyRole('admin', 'analyst'));

  protected async logout(): Promise<void> {
    await this.auth.logout();
  }
}
