import { CommonModule, CurrencyPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { ManagementReport } from '../../models';

@Component({
  selector: 'app-management-page',
  standalone: true,
  imports: [CommonModule, FormsModule, CurrencyPipe],
  templateUrl: './management-page.component.html',
  styleUrl: './management-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ManagementPageComponent {
  private readonly api = inject(CrmApiService);

  protected readonly loading = signal(true);
  protected readonly report = signal<ManagementReport | null>(null);
  protected readonly range = signal('90d');
  protected readonly maxPipeline = computed(() => Math.max(...this.report()?.pipeline.map((item) => item.count) ?? [1]));
  protected readonly maxSellerWins = computed(() => Math.max(...this.report()?.sellers.map((item) => item.wonCount) ?? [1]));

  constructor() {
    void this.load();
  }

  protected async updateRange(range: string): Promise<void> {
    this.range.set(range);
    await this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      const report = await firstValueFrom(this.api.getManagementReport({ range: this.range() }));
      this.report.set(report);
    } finally {
      this.loading.set(false);
    }
  }
}
