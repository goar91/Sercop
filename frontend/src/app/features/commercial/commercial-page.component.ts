import { CommonModule, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from '../../crm-api.service';
import { AuthService } from '../../core/auth.service';
import {
  CommercialAlertSummary,
  OpportunityActivity,
  OpportunityDetail,
  OpportunityListItem,
  OpportunityVisibility,
  SavedView,
  User,
  Zone,
} from '../../models';

type CommercialGrouping = 'age' | 'status';

@Component({
  selector: 'app-commercial-page',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe],
  templateUrl: './commercial-page.component.html',
  styleUrl: './commercial-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CommercialPageComponent {
  private readonly api = inject(CrmApiService);
  private readonly auth = inject(AuthService);
  private readonly ecuadorDateFormatter = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Guayaquil',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  });

  protected readonly loading = signal(true);
  protected readonly detailLoading = signal(false);
  protected readonly opportunities = signal<OpportunityListItem[]>([]);
  protected readonly zones = signal<Zone[]>([]);
  protected readonly users = signal<User[]>([]);
  protected readonly savedViews = signal<SavedView[]>([]);
  protected readonly activities = signal<OpportunityActivity[]>([]);
  protected readonly selectedOpportunity = signal<OpportunityDetail | null>(null);
  protected readonly alertSummary = signal<CommercialAlertSummary | null>(null);
  protected readonly totalCount = signal(0);
  protected readonly page = signal(1);
  protected readonly pageSize = signal(25);
  protected readonly errorMessage = signal('');
  protected readonly supportVisibility = signal<OpportunityVisibility | null>(null);
  protected readonly grouping = signal<CommercialGrouping>('age');
  protected readonly currentUser = this.auth.currentUser;
  protected readonly isSeller = computed(() => this.auth.hasAnyRole('seller'));
  protected readonly canManageAssignments = computed(() => this.auth.hasAnyRole('admin', 'gerencia', 'coordinator'));
  protected readonly canConfirmInvitation = this.canManageAssignments;
  protected readonly sellerBanner = computed(() => this.isSeller() ? 'Solo estas viendo los procesos asignados a tu usuario vendedor.' : '');
  protected readonly visibleSellerUsers = computed(() => this.users().filter((user) => user.role === 'seller' && user.active));
  protected readonly commercialAlerts = computed(() => this.alertSummary()?.items ?? []);
  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / this.pageSize())));
  protected readonly selectedOpportunityId = computed(() => this.selectedOpportunity()?.id ?? null);
  protected readonly workspaceHighlights = computed(() => {
    const items = this.opportunities();
    const invited = items.filter((item) => item.isInvitedMatch).length;
    const pending = items.filter((item) => item.hasPendingAction).length;
    const unassigned = items.filter((item) => !item.assignedUserName).length;
    const overdue = items.filter((item) => item.daysOpen > 1 && !item.isInvitedMatch).length;
    const criticalAlerts = this.alertSummary()?.criticalAlerts ?? 0;

    return [
      { label: 'Visibles en pagina', value: items.length, tone: 'neutral' },
      { label: 'Invitados HDM', value: invited, tone: 'invited' },
      { label: 'Sin asignar', value: unassigned, tone: 'warning' },
      { label: 'Mas de 1 dia', value: overdue, tone: 'aging' },
      { label: 'Con accion pendiente', value: pending, tone: 'priority' },
      { label: 'Alertas criticas', value: criticalAlerts, tone: 'critical' },
    ];
  });
  protected readonly activeFilterSummary = computed(() => {
    const filters = this.filters();
    const tokens: string[] = [];

    if (filters.search.trim()) {
      tokens.push(`Busqueda: ${filters.search.trim()}`);
    }

    if (filters.estado) {
      tokens.push(`Estado: ${this.humanizeLabel(filters.estado)}`);
    }

    if (filters.zoneId) {
      const zone = this.zones().find((item) => item.id === Number(filters.zoneId));
      if (zone) {
        tokens.push(`Zona: ${zone.name}`);
      }
    }

    if (filters.assignedUserId) {
      const user = this.users().find((item) => item.id === Number(filters.assignedUserId));
      if (user) {
        tokens.push(`Vendedor: ${user.fullName}`);
      }
    }

    if (filters.invitedOnly) {
      tokens.push('Solo invitados HDM');
    }

    if (filters.todayOnly) {
      tokens.push('Solo fecha actual');
    }

    return tokens;
  });
  protected readonly boardTitle = computed(() => this.grouping() === 'age' ? 'Tablero por antiguedad' : 'Tablero por estado comercial');
  protected readonly boardDescription = computed(() => this.grouping() === 'age'
    ? 'Prioriza invitaciones HDM y separa la operacion entre procesos recientes y backlog.'
    : 'Revisa la carga por estado comercial para mover el pipeline con menos friccion.');

  protected readonly filters = signal({
    search: '',
    estado: '',
    zoneId: '',
    assignedUserId: '',
    invitedOnly: false,
    todayOnly: false,
  });

  protected readonly assignmentForm = signal({
    assignedUserId: '',
    zoneId: '',
    estado: 'asignado',
    priority: 'normal',
    notes: '',
  });

  protected readonly noteForm = signal({
    body: '',
  });

  protected readonly reminderForm = signal({
    remindAt: '',
    notes: '',
  });

  protected readonly invitationForm = signal({
    isInvitedMatch: false,
    invitationSource: 'manual_crm',
    invitationEvidenceUrl: '',
    invitationNotes: '',
  });

  protected readonly savedViewForm = signal({
    name: '',
    shared: false,
  });

  protected readonly visibilityCode = signal('');

  protected readonly invitedLane = computed(() => this.opportunities().filter((item) => item.isInvitedMatch));
  protected readonly newProcessesLane = computed(() =>
    this.opportunities()
      .filter((item) => !item.isInvitedMatch && this.isPublishedTodayInEcuador(item))
      .sort((a, b) => this.getPublicationTimestamp(b) - this.getPublicationTimestamp(a))
  );
  protected readonly currentLane = computed(() =>
    this.opportunities()
      .filter((item) => !item.isInvitedMatch && !this.isPublishedTodayInEcuador(item) && item.daysOpen <= 1)
      .sort((a, b) => this.getPublicationTimestamp(b) - this.getPublicationTimestamp(a))
  );
  protected readonly staleLane = computed(() =>
    this.opportunities()
      .filter((item) => !item.isInvitedMatch && item.daysOpen > 1)
      .sort((a, b) => this.getPublicationTimestamp(b) - this.getPublicationTimestamp(a))
  );
  protected readonly statusGroups = computed(() => {
    const groups = new Map<string, OpportunityListItem[]>();
    for (const item of this.opportunities()) {
      const key = item.estado || 'sin_estado';
      const existing = groups.get(key) ?? [];
      existing.push(item);
      groups.set(key, existing);
    }

    return Array.from(groups.entries()).map(([label, items]) => ({ label, items }));
  });

  constructor() {
    void this.initialize();
  }

  private isPublishedTodayInEcuador(item: OpportunityListItem): boolean {
    return this.getEcuadorDateKey(item.fechaPublicacion) === this.getEcuadorTodayKey();
  }

  private getPublicationTimestamp(item: OpportunityListItem): number {
    return item.fechaPublicacion ? new Date(item.fechaPublicacion).getTime() : 0;
  }

  private getEcuadorTodayKey(): string {
    return this.getEcuadorDateKey(new Date().toISOString()) ?? '';
  }

  private getEcuadorDateKey(value?: string | null): string | null {
    if (!value) {
      return null;
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return null;
    }

    const parts = this.ecuadorDateFormatter.formatToParts(date);
    const year = parts.find((part) => part.type === 'year')?.value;
    const month = parts.find((part) => part.type === 'month')?.value;
    const day = parts.find((part) => part.type === 'day')?.value;
    return year && month && day ? `${year}-${month}-${day}` : null;
  }

  protected patchFilters<K extends keyof ReturnType<typeof this.filters>>(field: K, value: ReturnType<typeof this.filters>[K]): void {
    this.filters.update((current) => ({ ...current, [field]: value }));
  }

  protected patchAssignmentForm<K extends keyof ReturnType<typeof this.assignmentForm>>(field: K, value: ReturnType<typeof this.assignmentForm>[K]): void {
    this.assignmentForm.update((current) => ({ ...current, [field]: value }));
  }

  protected patchReminderForm<K extends keyof ReturnType<typeof this.reminderForm>>(field: K, value: ReturnType<typeof this.reminderForm>[K]): void {
    this.reminderForm.update((current) => ({ ...current, [field]: value }));
  }

  protected patchInvitationForm<K extends keyof ReturnType<typeof this.invitationForm>>(field: K, value: ReturnType<typeof this.invitationForm>[K]): void {
    this.invitationForm.update((current) => ({ ...current, [field]: value }));
  }

  protected patchSavedViewForm<K extends keyof ReturnType<typeof this.savedViewForm>>(field: K, value: ReturnType<typeof this.savedViewForm>[K]): void {
    this.savedViewForm.update((current) => ({ ...current, [field]: value }));
  }

  protected patchNoteForm<K extends keyof ReturnType<typeof this.noteForm>>(field: K, value: ReturnType<typeof this.noteForm>[K]): void {
    this.noteForm.update((current) => ({ ...current, [field]: value }));
  }

  protected async reload(): Promise<void> {
    this.page.set(1);
    await this.loadList();
  }

  protected async selectOpportunity(item: OpportunityListItem): Promise<void> {
    this.detailLoading.set(true);
    try {
      const [detail, activities] = await Promise.all([
        firstValueFrom(this.api.getOpportunity(item.id)),
        firstValueFrom(this.api.getOpportunityActivities(item.id, 1, 30)),
      ]);

      this.selectedOpportunity.set(detail);
      this.activities.set(activities.items);
      this.assignmentForm.set({
        assignedUserId: detail.assignedUserId?.toString() ?? '',
        zoneId: detail.zoneId?.toString() ?? '',
        estado: detail.estado ?? 'asignado',
        priority: detail.priority ?? 'normal',
        notes: detail.crmNotes ?? '',
      });
      this.reminderForm.set({
        remindAt: detail.reminder?.remindAt?.slice(0, 16) ?? '',
        notes: detail.reminder?.notes ?? '',
      });
      this.invitationForm.set({
        isInvitedMatch: detail.isInvitedMatch,
        invitationSource: detail.invitationSource ?? 'manual_crm',
        invitationEvidenceUrl: detail.invitationEvidenceUrl ?? '',
        invitationNotes: detail.invitationNotes ?? '',
      });
    } finally {
      this.detailLoading.set(false);
    }
  }

  protected async saveAssignment(): Promise<void> {
    const detail = this.selectedOpportunity();
    if (!detail) {
      return;
    }

    const form = this.assignmentForm();
    const updated = await firstValueFrom(this.api.updateAssignment(detail.id, {
      assignedUserId: form.assignedUserId ? Number(form.assignedUserId) : null,
      zoneId: form.zoneId ? Number(form.zoneId) : null,
      estado: form.estado || null,
      priority: form.priority || null,
      notes: form.notes || null,
    }));

    this.selectedOpportunity.set(updated);
    await this.loadList();
  }

  protected async saveNote(): Promise<void> {
    const detail = this.selectedOpportunity();
    const body = this.noteForm().body.trim();
    if (!detail || !body) {
      return;
    }

    await firstValueFrom(this.api.createOpportunityActivity(detail.id, {
      activityType: 'note',
      body,
      metadataJson: '{}',
    }));

    this.noteForm.set({ body: '' });
    await this.selectOpportunity({
      ...detail,
      daysOpen: detail.daysOpen,
      agingBucket: detail.agingBucket,
      hasPendingAction: detail.hasPendingAction,
      slaStatus: detail.slaStatus,
    });
  }

  protected async saveReminder(): Promise<void> {
    const detail = this.selectedOpportunity();
    if (!detail) {
      return;
    }

    await firstValueFrom(this.api.upsertReminder(detail.id, {
      remindAt: this.reminderForm().remindAt ? new Date(this.reminderForm().remindAt).toISOString() : null,
      notes: this.reminderForm().notes || null,
    }));

    await this.selectOpportunity({
      ...detail,
      daysOpen: detail.daysOpen,
      agingBucket: detail.agingBucket,
      hasPendingAction: detail.hasPendingAction,
      slaStatus: detail.slaStatus,
    });
    await this.loadList();
  }

  protected async saveInvitation(): Promise<void> {
    const detail = this.selectedOpportunity();
    if (!detail) {
      return;
    }

    const updated = await firstValueFrom(this.api.updateInvitation(detail.id, this.invitationForm()));
    this.selectedOpportunity.set(updated);
    await this.loadList();
  }

  protected async applySavedView(view: SavedView): Promise<void> {
    try {
      const parsed = JSON.parse(view.filtersJson) as {
        search?: string;
        estado?: string;
        zoneId?: string;
        assignedUserId?: string;
        invitedOnly?: boolean;
        todayOnly?: boolean;
        grouping?: CommercialGrouping;
      };

      this.filters.set({
        search: parsed.search ?? '',
        estado: parsed.estado ?? '',
        zoneId: parsed.zoneId ?? '',
        assignedUserId: parsed.assignedUserId ?? '',
        invitedOnly: parsed.invitedOnly ?? false,
        todayOnly: parsed.todayOnly ?? false,
      });
      this.grouping.set(parsed.grouping ?? 'age');
      this.page.set(1);
      await this.loadList();
    } catch {
      this.errorMessage.set('La vista guardada no se pudo leer.');
    }
  }

  protected async saveCurrentView(): Promise<void> {
    const payload = {
      viewType: 'commercial',
      name: this.savedViewForm().name,
      shared: this.savedViewForm().shared,
      filtersJson: JSON.stringify({
        ...this.filters(),
        grouping: this.grouping(),
      }),
    };

    await firstValueFrom(this.api.createSavedView(payload));
    this.savedViewForm.set({ name: '', shared: false });
    await this.loadSavedViews();
  }

  protected async deleteSavedView(view: SavedView): Promise<void> {
    await firstValueFrom(this.api.deleteSavedView(view.id));
    await this.loadSavedViews();
  }

  protected async changePage(delta: number): Promise<void> {
    const nextPage = this.page() + delta;
    if (nextPage < 1 || nextPage > this.totalPages()) {
      return;
    }

    this.page.set(nextPage);
    await this.loadList();
  }

  protected export(format: 'csv' | 'excel'): void {
    window.open(this.api.exportOpportunitiesUrl({
      format,
      ...this.filters(),
      zoneId: this.filters().zoneId ? Number(this.filters().zoneId) : null,
      assignedUserId: this.filters().assignedUserId ? Number(this.filters().assignedUserId) : null,
    }), '_blank');
  }

  protected async runVisibilityCheck(): Promise<void> {
    const code = this.visibilityCode().trim();
    if (!code) {
      return;
    }

    this.supportVisibility.set(await firstValueFrom(this.api.getOpportunityVisibility(code, this.filters().todayOnly)));
  }

  protected openProcess(url: string): void {
    if (!url) {
      return;
    }

    window.open(url, '_blank', 'noopener,noreferrer');
  }

  protected humanizeLabel(value: string | null | undefined): string {
    if (!value) {
      return 'Sin dato';
    }

    return value
      .replace(/_/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();
  }

  protected priorityTone(priority: string | null | undefined): string {
    const normalized = (priority ?? '').trim().toLowerCase();
    switch (normalized) {
      case 'alta':
        return 'high';
      case 'baja':
        return 'low';
      default:
        return 'normal';
    }
  }

  protected slaTone(slaStatus: string | null | undefined): string {
    const normalized = (slaStatus ?? '').toLowerCase();
    if (normalized.includes('crit') || normalized.includes('venc')) {
      return 'high';
    }

    if (normalized.includes('alert') || normalized.includes('pend')) {
      return 'warning';
    }

    return 'normal';
  }

  protected alertTone(severity: string | null | undefined): string {
    return (severity ?? '').toLowerCase() === 'critical' ? 'high' : 'warning';
  }

  protected alertSeverityLabel(severity: string | null | undefined): string {
    return (severity ?? '').toLowerCase() === 'critical' ? 'Critica' : 'Advertencia';
  }

  private async initialize(): Promise<void> {
    this.loading.set(true);
    try {
      await Promise.all([this.loadDependencies(), this.loadList()]);
    } catch (error) {
      this.errorMessage.set(this.extractError(error));
    } finally {
      this.loading.set(false);
    }
  }

  private async loadDependencies(): Promise<void> {
    const [zones, users] = await Promise.all([
      firstValueFrom(this.api.getZones()),
      firstValueFrom(this.api.getUsers(1, 100)),
    ]);

    this.zones.set(zones);
    this.users.set(users.items);
    await this.loadSavedViews();
  }

  private async loadSavedViews(): Promise<void> {
    const views = await firstValueFrom(this.api.getSavedViews('commercial', 1, 30));
    this.savedViews.set(views.items);
  }

  private async loadList(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set('');
    try {
      const filters = this.filters();
      const [response, alerts] = await Promise.all([
        firstValueFrom(this.api.getOpportunities({
          search: filters.search || undefined,
          estado: filters.estado || undefined,
          zoneId: filters.zoneId ? Number(filters.zoneId) : null,
          assignedUserId: filters.assignedUserId ? Number(filters.assignedUserId) : null,
          invitedOnly: filters.invitedOnly,
          todayOnly: filters.todayOnly,
          page: this.page(),
          pageSize: this.pageSize(),
        })),
        firstValueFrom(this.api.getCommercialAlerts()),
      ]);

      this.opportunities.set(response.items);
      this.totalCount.set(response.totalCount);
      this.alertSummary.set(alerts);

      const selectedId = this.selectedOpportunityId();
      const selectedStillVisible = selectedId ? response.items.some((item) => item.id === selectedId) : false;
      if (!selectedStillVisible) {
        this.selectedOpportunity.set(null);
        this.activities.set([]);
      }

      if (!this.selectedOpportunity() && response.items.length > 0) {
        await this.selectOpportunity(response.items[0]);
      }
    } catch (error) {
      this.errorMessage.set(this.extractError(error));
    } finally {
      this.loading.set(false);
    }
  }

  private extractError(error: unknown): string {
    if (error instanceof HttpErrorResponse && typeof error.error?.detail === 'string') {
      return error.error.detail;
    }

    return 'No se pudo completar la operacion.';
  }
}
