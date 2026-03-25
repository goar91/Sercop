import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { WorkflowDetail, WorkflowSummary } from '../../models';

@Component({
  selector: 'app-workflows-page',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './workflows-page.component.html',
  styleUrl: './workflows-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkflowsPageComponent {
  private readonly api = inject(CrmApiService);

  protected readonly loading = signal(true);
  protected readonly workflows = signal<WorkflowSummary[]>([]);
  protected readonly workflowTotal = signal(0);
  protected readonly page = signal(1);
  protected readonly selectedWorkflow = signal<WorkflowDetail | null>(null);
  protected readonly activeCount = computed(() => this.workflows().filter((item) => item.active).length);

  constructor() {
    void this.load();
  }

  protected async selectWorkflow(workflow: WorkflowSummary): Promise<void> {
    this.selectedWorkflow.set(await firstValueFrom(this.api.getWorkflow(workflow.id)));
  }

  protected async changePage(delta: number): Promise<void> {
    const nextPage = this.page() + delta;
    if (nextPage < 1) {
      return;
    }

    this.page.set(nextPage);
    await this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      const response = await firstValueFrom(this.api.getWorkflows(this.page(), 18));
      this.workflows.set(response.items);
      this.workflowTotal.set(response.totalCount);
      if (!this.selectedWorkflow() && response.items.length > 0) {
        await this.selectWorkflow(response.items[0]);
      }
    } finally {
      this.loading.set(false);
    }
  }
}
