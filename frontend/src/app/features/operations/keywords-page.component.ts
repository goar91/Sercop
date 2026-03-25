import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { KeywordRule } from '../../models';

@Component({
  selector: 'app-keywords-page',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './keywords-page.component.html',
  styleUrl: './keywords-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KeywordsPageComponent {
  private readonly api = inject(CrmApiService);

  protected readonly rules = signal<KeywordRule[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);

  protected readonly filters = signal({
    ruleType: '' as '' | 'include' | 'exclude',
    scope: '' as '' | 'all' | 'ocds' | 'nco',
  });

  protected readonly form = signal({
    id: null as number | null,
    ruleType: 'include' as 'include' | 'exclude',
    scope: 'all' as 'all' | 'ocds' | 'nco',
    keyword: '',
    family: '',
    weight: 1,
    notes: '',
    active: true,
  });

  constructor() {
    void this.load();
  }

  protected patchFilters<K extends keyof ReturnType<typeof this.filters>>(field: K, value: ReturnType<typeof this.filters>[K]): void {
    this.filters.update((current) => ({ ...current, [field]: value }));
  }

  protected patchForm<K extends keyof ReturnType<typeof this.form>>(field: K, value: ReturnType<typeof this.form>[K]): void {
    this.form.update((current) => ({ ...current, [field]: value }));
  }

  protected async applyFilters(): Promise<void> {
    this.page.set(1);
    await this.load();
  }

  protected editRule(rule: KeywordRule): void {
    this.form.set({
      id: rule.id,
      ruleType: rule.ruleType,
      scope: rule.scope,
      keyword: rule.keyword,
      family: rule.family ?? '',
      weight: rule.weight,
      notes: rule.notes ?? '',
      active: rule.active,
    });
  }

  protected resetForm(): void {
    this.form.set({
      id: null,
      ruleType: 'include',
      scope: 'all',
      keyword: '',
      family: '',
      weight: 1,
      notes: '',
      active: true,
    });
  }

  protected async saveRule(): Promise<void> {
    const form = this.form();
    const payload = {
      ruleType: form.ruleType,
      scope: form.scope,
      keyword: form.keyword,
      family: form.family || null,
      weight: Number(form.weight),
      notes: form.notes || null,
      active: form.active,
    };

    if (form.id) {
      await firstValueFrom(this.api.updateKeywordRule(form.id, payload));
    } else {
      await firstValueFrom(this.api.createKeywordRule(payload));
    }

    this.resetForm();
    await this.load();
  }

  protected async deleteRule(id: number): Promise<void> {
    await firstValueFrom(this.api.deleteKeywordRule(id));
    await this.load();
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
      const response = await firstValueFrom(this.api.getKeywordRules({
        ruleType: this.filters().ruleType || undefined,
        scope: this.filters().scope || undefined,
        page: this.page(),
        pageSize: 20,
      }));
      this.rules.set(response.items);
      this.total.set(response.totalCount);
    } finally {
      this.loading.set(false);
    }
  }
}
