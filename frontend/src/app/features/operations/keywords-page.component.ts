import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { AuthService } from '../../core/auth.service';
import { KeywordRefreshRun, KeywordRule, SercopCredentialStatus } from '../../models';

@Component({
  selector: 'app-keywords-page',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatIconModule,
  ],
  templateUrl: './keywords-page.component.html',
  styleUrl: './keywords-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KeywordsPageComponent implements OnDestroy {
  private readonly api = inject(CrmApiService);
  private readonly auth = inject(AuthService);
  private refreshPollHandle: ReturnType<typeof setTimeout> | null = null;

  protected readonly rules = signal<KeywordRule[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly refreshLoading = signal(true);
  protected readonly refreshRun = signal<KeywordRefreshRun | null>(null);
  protected readonly sercopStatusLoading = signal(false);
  protected readonly sercopStatus = signal<SercopCredentialStatus | null>(null);
  protected readonly sercopSaving = signal(false);
  protected readonly sercopTesting = signal(false);
  protected readonly sercopClearing = signal(false);
  protected readonly sercopFeedback = signal<string | null>(null);
  protected readonly sercopError = signal<string | null>(null);
  protected readonly isAdmin = computed(() => this.auth.hasAnyRole('admin'));
  protected readonly canDelete = this.auth.hasAnyRole('admin', 'coordinator');
  protected readonly sercopBusy = computed(() => this.sercopSaving() || this.sercopTesting() || this.sercopClearing());
  protected readonly refreshRunning = computed(() => {
    const status = this.refreshRun()?.status ?? '';
    return status === 'pending' || status === 'running';
  });

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

  protected readonly sercopForm = signal({
    ruc: '',
    userName: '',
    password: '',
  });

  constructor() {
    effect(() => {
      if (this.isAdmin()) {
        void this.loadSercopStatus();
      }
    });

    void this.loadAll();
  }

  ngOnDestroy(): void {
    if (this.refreshPollHandle) {
      clearTimeout(this.refreshPollHandle);
      this.refreshPollHandle = null;
    }
  }

  protected patchFilters<K extends keyof ReturnType<typeof this.filters>>(field: K, value: ReturnType<typeof this.filters>[K]): void {
    this.filters.update((current) => ({ ...current, [field]: value }));
  }

  protected patchForm<K extends keyof ReturnType<typeof this.form>>(field: K, value: ReturnType<typeof this.form>[K]): void {
    this.form.update((current) => ({ ...current, [field]: value }));
  }

  protected patchSercopForm<K extends keyof ReturnType<typeof this.sercopForm>>(field: K, value: ReturnType<typeof this.sercopForm>[K]): void {
    this.sercopForm.update((current) => ({ ...current, [field]: value }));
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
    await this.loadAll();
  }

  protected async deleteRule(id: number): Promise<void> {
    if (!this.canDelete) {
      return;
    }

    await firstValueFrom(this.api.deleteKeywordRule(id));
    await this.loadAll();
  }

  protected async runRefresh(): Promise<void> {
    this.refreshLoading.set(true);
    this.refreshRun.set(await firstValueFrom(this.api.triggerKeywordRefresh()));
    this.scheduleRefreshPoll();
  }

  protected async changePage(delta: number): Promise<void> {
    const nextPage = this.page() + delta;
    if (nextPage < 1) {
      return;
    }

    this.page.set(nextPage);
    await this.load();
  }

  protected async saveSercopCredentials(): Promise<void> {
    if (!this.isAdmin()) {
      return;
    }

    const form = this.sercopForm();
    const payload = {
      ruc: form.ruc.trim(),
      userName: form.userName.trim(),
      password: form.password,
    };

    if (!payload.ruc || !payload.userName || !payload.password) {
      this.sercopError.set('Debes ingresar RUC, usuario y clave antes de guardar.');
      this.sercopFeedback.set(null);
      return;
    }

    this.sercopSaving.set(true);
    this.sercopError.set(null);
    this.sercopFeedback.set(null);
    try {
      const status = await firstValueFrom(this.api.upsertSercopCredentials(payload));
      this.sercopStatus.set(status);
      this.resetSercopForm();
      this.sercopFeedback.set(
        status.validationStatus === 'failed'
          ? 'Credencial guardada en la base de datos, pero la validacion contra el portal SERCOP fallo.'
          : 'Credencial guardada y protegida en la base de datos.'
      );
    } catch (error) {
      this.sercopError.set(this.formatApiError(error, 'No se pudo guardar la credencial SERCOP.'));
    } finally {
      this.sercopSaving.set(false);
    }
  }

  protected async testSercopCredentials(): Promise<void> {
    if (!this.isAdmin()) {
      return;
    }

    this.sercopTesting.set(true);
    this.sercopError.set(null);
    this.sercopFeedback.set(null);
    try {
      const status = await firstValueFrom(this.api.testSercopCredentials());
      this.sercopStatus.set(status);
      this.sercopFeedback.set(
        status.validationStatus === 'failed'
          ? 'La cuenta esta guardada, pero la autenticacion en SERCOP sigue fallando.'
          : 'La cuenta almacenada pudo autenticarse en SERCOP correctamente.'
      );
    } catch (error) {
      this.sercopError.set(this.formatApiError(error, 'No se pudo probar la credencial SERCOP guardada.'));
    } finally {
      this.sercopTesting.set(false);
    }
  }

  protected async clearSercopCredentials(): Promise<void> {
    if (!this.isAdmin() || !window.confirm('Se eliminara la cuenta SERCOP compartida guardada. Deseas continuar?')) {
      return;
    }

    this.sercopClearing.set(true);
    this.sercopError.set(null);
    this.sercopFeedback.set(null);
    try {
      await firstValueFrom(this.api.clearSercopCredentials());
      this.resetSercopForm();
      await this.loadSercopStatus();
      this.sercopFeedback.set('La cuenta SERCOP compartida fue eliminada de la base de datos.');
    } catch (error) {
      this.sercopError.set(this.formatApiError(error, 'No se pudo eliminar la credencial SERCOP.'));
    } finally {
      this.sercopClearing.set(false);
    }
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

  private async loadRefreshStatus(): Promise<void> {
    this.refreshLoading.set(true);
    try {
      this.refreshRun.set(await firstValueFrom(this.api.getKeywordRefreshStatus()));
    } finally {
      this.refreshLoading.set(false);
    }

    this.scheduleRefreshPoll();
  }

  private async loadAll(): Promise<void> {
    await Promise.all([this.load(), this.loadRefreshStatus()]);
  }

  private scheduleRefreshPoll(): void {
    if (this.refreshPollHandle) {
      clearTimeout(this.refreshPollHandle);
      this.refreshPollHandle = null;
    }

    if (!this.refreshRunning()) {
      return;
    }

    this.refreshPollHandle = setTimeout(() => {
      void this.loadRefreshStatus();
    }, 4000);
  }

  private async loadSercopStatus(): Promise<void> {
    if (!this.isAdmin()) {
      this.sercopStatus.set(null);
      return;
    }

    this.sercopStatusLoading.set(true);
    try {
      this.sercopStatus.set(await firstValueFrom(this.api.getSercopCredentialStatus()));
    } finally {
      this.sercopStatusLoading.set(false);
    }
  }

  private resetSercopForm(): void {
    this.sercopForm.set({
      ruc: '',
      userName: '',
      password: '',
    });
  }

  private formatApiError(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      if (typeof error.error === 'string' && error.error.trim()) {
        return error.error;
      }

      if (error.error && typeof error.error === 'object') {
        const response = error.error as {
          title?: string;
          detail?: string;
          errors?: Record<string, string[] | string>;
        };

        if (response.errors) {
          const messages = Object.values(response.errors)
            .flatMap((value) => Array.isArray(value) ? value : [value])
            .map((value) => value?.toString().trim())
            .filter((value): value is string => Boolean(value));
          if (messages.length > 0) {
            return messages.join(' ');
          }
        }

        if (response.detail?.trim()) {
          return response.detail.trim();
        }

        if (response.title?.trim()) {
          return response.title.trim();
        }
      }

      if (error.message.trim()) {
        return error.message.trim();
      }
    }

    if (error instanceof Error && error.message.trim()) {
      return error.message.trim();
    }

    return fallback;
  }
}
