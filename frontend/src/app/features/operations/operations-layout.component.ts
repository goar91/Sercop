import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { MatToolbarModule } from '@angular/material/toolbar';
import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-operations-layout',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    MatButtonModule,
    MatDividerModule,
    MatIconModule,
    MatTabsModule,
    MatToolbarModule,
  ],
  templateUrl: './operations-layout.component.html',
  styleUrl: './operations-layout.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationsLayoutComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly canAccessAdminModules = computed(() => this.auth.hasAnyRole('admin'));
  protected readonly canManageKeywords = computed(() => this.auth.hasAnyRole('admin', 'coordinator'));

  constructor() {
    queueMicrotask(() => {
      if (this.route.firstChild) {
        return;
      }

      void this.router.navigate([this.canAccessAdminModules() ? 'users-zones' : 'keywords'], {
        relativeTo: this.route,
        replaceUrl: true,
      });
    });
  }
}
