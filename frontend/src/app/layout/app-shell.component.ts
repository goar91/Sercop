import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatBadgeModule } from '@angular/material/badge';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDividerModule } from '@angular/material/divider';
import { AuthService } from '../core/auth.service';
import { ThemeMode, ThemeService } from '../core/theme.service';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    MatToolbarModule,
    MatSidenavModule,
    MatListModule,
    MatIconModule,
    MatButtonModule,
    MatMenuModule,
    MatBadgeModule,
    MatTooltipModule,
    MatDividerModule,
  ],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShellComponent {
  private readonly auth = inject(AuthService);
  private readonly theme = inject(ThemeService);

  protected readonly user = this.auth.currentUser;
  protected readonly sidenavOpen = signal(true);
  protected readonly themeMode = this.theme.mode;
  protected readonly effectiveTheme = this.theme.effectiveTheme;
  protected readonly showManagement = computed(() => this.auth.hasAnyRole('admin', 'gerencia'));
  protected readonly showOperations = computed(() => this.auth.hasAnyRole('admin', 'coordinator'));
  protected readonly showAutomation = computed(() => this.auth.hasAnyRole('admin', 'analyst'));
  protected readonly showCommercialAll = computed(() =>
    this.auth.hasAnyRole('admin', 'gerencia', 'coordinator')
    || this.user()?.loginName?.toLowerCase() === 'importaciones'
  );
  protected readonly operationsLink = computed(() =>
    this.auth.hasAnyRole('admin') ? '/operations/users-zones' : '/operations/keywords'
  );
  protected readonly operationsTitle = computed(() =>
    this.auth.hasAnyRole('admin') ? 'Operación' : 'Palabras clave'
  );

  protected toggleSidenav(): void {
    this.sidenavOpen.update(value => !value);
  }

  protected setThemeMode(mode: ThemeMode): void {
    this.theme.setMode(mode);
  }

  protected themeIcon(): string {
    const mode = this.themeMode();
    if (mode === 'system') {
      return 'devices';
    }

    return mode === 'dark' ? 'dark_mode' : 'light_mode';
  }

  protected themeModeLabel(mode: ThemeMode): string {
    switch (mode) {
      case 'dark':
        return 'Oscuro';
      case 'light':
        return 'Claro';
      default:
        return 'Sistema';
    }
  }

  protected closeSidenav(): void {
    this.sidenavOpen.set(false);
  }

  protected async logout(): Promise<void> {
    await this.auth.logout();
  }
}
