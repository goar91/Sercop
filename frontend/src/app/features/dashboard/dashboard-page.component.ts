import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTabsModule } from '@angular/material/tabs';
import { MatToolbarModule } from '@angular/material/toolbar';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { AuthService } from '../../core/auth.service';
import { DashboardSummary, MetaInfo } from '../../models';

@Component({
  selector: 'app-dashboard-page',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatButtonModule,
    MatCardModule,
    MatDividerModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTabsModule,
    MatToolbarModule,
  ],
  templateUrl: './dashboard-page.component.html',
  styleUrl: './dashboard-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardPageComponent {
  private readonly api = inject(CrmApiService);
  private readonly auth = inject(AuthService);

  protected readonly meta = signal<MetaInfo | null>(null);
  protected readonly summary = signal<DashboardSummary | null>(null);
  protected readonly loading = signal(true);
  protected readonly showManagement = computed(() => this.auth.hasAnyRole('admin', 'gerencia'));
  protected readonly showOperations = computed(() => this.auth.hasAnyRole('admin', 'coordinator'));
  protected readonly showAutomation = computed(() => this.auth.hasAnyRole('admin', 'analyst'));
  protected readonly showUsersAndZonesShortcut = computed(() => this.auth.hasAnyRole('admin'));
  protected readonly operationsShortcutLink = computed(() => this.auth.hasAnyRole('admin') ? '/operations/users-zones' : '/operations/keywords');
  protected readonly operationsShortcutLabel = computed(() => this.auth.hasAnyRole('admin') ? 'Configurar operacion' : 'Gestionar palabras clave');
  protected readonly operationsShortcutDescription = computed(() => this.auth.hasAnyRole('admin') ? 'Usuarios, zonas y reglas de operacion' : 'Editar reglas y taxonomia comercial');

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      const [meta, summary] = await Promise.all([
        firstValueFrom(this.api.getMeta()),
        firstValueFrom(this.api.getDashboard()),
      ]);
      this.meta.set(meta);
      this.summary.set(summary);
    } finally {
      this.loading.set(false);
    }
  }
}
