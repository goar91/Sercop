import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { DashboardSummary, MetaInfo } from '../../models';

@Component({
  selector: 'app-dashboard-page',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './dashboard-page.component.html',
  styleUrl: './dashboard-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardPageComponent {
  private readonly api = inject(CrmApiService);

  protected readonly meta = signal<MetaInfo | null>(null);
  protected readonly summary = signal<DashboardSummary | null>(null);
  protected readonly loading = signal(true);

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
