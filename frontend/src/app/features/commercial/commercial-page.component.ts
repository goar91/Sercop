import { CommonModule, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

import { CrmApiService } from '../../crm-api.service';
import { AuthService } from '../../core/auth.service';
import {
  CommercialAlertSummary,
  OpportunityActivity,
  OpportunityDetail,
  OpportunityListItem,
  OpportunityProcessCategory,
  OpportunityVisibility,
  SavedView,
  SercopOperationalStatus,
  User,
  Zone,
} from '../../models';

type CommercialGrouping = 'age' | 'status';
type CommercialFilters = {
  search: string;
  entity: string;
  processCode: string;
  keyword: string;
  estado: string;
  zoneId: string;
  assignedUserId: string;
  processCategory: OpportunityProcessCategory;
  invitedOnly: boolean;
  todayOnly: boolean;
};

@Component({
  selector: 'app-commercial-page',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    DatePipe,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
  ],
  templateUrl: './commercial-page.component.html',
  styleUrl: './commercial-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CommercialPageComponent {
  @ViewChild('detailPanel') private detailPanel?: ElementRef<HTMLElement>;

  private readonly api = inject(CrmApiService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly ecuadorDateFormatter = new Intl.DateTimeFormat('en-US', {
    timeZone: 'America/Guayaquil',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  });
  private listRequestSequence = 0;
  private readonly scope = (this.route.snapshot.data['scope'] as 'chemistry' | 'all') ?? 'chemistry';

  protected readonly loading = signal(true);
  protected readonly detailLoading = signal(false);
  protected readonly opportunities = signal<OpportunityListItem[]>([]);
  protected readonly zones = signal<Zone[]>([]);
  protected readonly users = signal<User[]>([]);
  protected readonly savedViews = signal<SavedView[]>([]);
  protected readonly activities = signal<OpportunityActivity[]>([]);
  protected readonly selectedOpportunity = signal<OpportunityDetail | null>(null);
  protected readonly alertSummary = signal<CommercialAlertSummary | null>(null);
  protected readonly sercopStatus = signal<SercopOperationalStatus | null>(null);
  protected readonly totalCount = signal(0);
  protected readonly currentPage = signal(1);
  protected readonly pageSize = signal(25);
  protected readonly errorMessage = signal('');
  protected readonly supportVisibility = signal<OpportunityVisibility | null>(null);
  protected readonly visibilityImporting = signal(false);
  protected readonly grouping = signal<CommercialGrouping>('age');
  protected readonly currentUser = this.auth.currentUser;
  protected readonly isAllScope = computed(() => this.scope === 'all');
  protected readonly isSeller = computed(() => this.auth.hasAnyRole('seller'));
  protected readonly isImportOperator = computed(() => this.currentUser()?.loginName?.toLowerCase() === 'importaciones');
  protected readonly canManageAssignments = computed(() => this.auth.hasAnyRole('admin', 'gerencia', 'coordinator'));
  protected readonly canConfirmInvitation = this.canManageAssignments;
  protected readonly sellerBanner = computed(() => this.isSeller() ? 'Solo estas viendo los procesos asignados a tu usuario vendedor.' : '');
  protected readonly importOperatorBanner = computed(() => this.isImportOperator() ? 'Perfil importaciones: ves todos los procesos; solo se aplican los filtros que actives manualmente.' : '');
  protected readonly visibleSellerUsers = computed(() => this.users().filter((user) => user.role === 'seller' && user.active));
  protected readonly commercialAlerts = computed(() => this.alertSummary()?.items ?? []);
  protected readonly shouldShowAlertPanel = computed(() => !this.isSeller() || this.alertSummary()?.showForCurrentUser === true);
  protected readonly selectedOpportunityId = computed(() => this.selectedOpportunity()?.id ?? null);
  protected readonly processCategoryTabs: ReadonlyArray<{ value: OpportunityProcessCategory; label: string }> = [
    { value: 'all', label: 'Todos' },
    { value: 'infimas', label: 'Ínfimas' },
    { value: 'nco', label: 'NCO' },
    { value: 'sie', label: 'SIE' },
    { value: 're', label: 'Régimen Especial' },
  ];
  protected readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(Math.max(0, this.totalCount()) / Math.max(1, this.pageSize())))
  );
  protected readonly pageRangeLabel = computed(() => {
    const total = this.totalCount();
    const items = this.opportunities().length;
    const page = this.currentPage();
    const size = this.pageSize();
    if (total <= 0 || items <= 0) {
      return 'Sin procesos';
    }

    const start = (page - 1) * size + 1;
    const end = start + items - 1;
    return `Mostrando ${start}-${end} de ${total}`;
  });
  protected readonly workspaceHighlights = computed(() => {
    const items = this.opportunities();
    let invited = 0;
    let pending = 0;
    let unassigned = 0;
    let overdue = 0;

    for (const item of items) {
      if (item.isInvitedMatch) {
        invited += 1;
      }

      if (item.hasPendingAction) {
        pending += 1;
      }

      if (!item.assignedUserName) {
        unassigned += 1;
      }

      if (item.daysOpen > 1 && !item.isInvitedMatch) {
        overdue += 1;
      }
    }

    const criticalAlerts = this.alertSummary()?.criticalAlerts ?? 0;

    return [
      { label: 'Visibles en tablero', value: this.totalCount(), tone: 'neutral' },
      { label: 'Invitados (pagina)', value: invited, tone: 'invited' },
      { label: 'Sin asignar (pagina)', value: unassigned, tone: 'warning' },
      { label: 'Mas de 1 dia (pagina)', value: overdue, tone: 'aging' },
      { label: 'Accion pendiente (pagina)', value: pending, tone: 'priority' },
      { label: 'Alertas criticas', value: criticalAlerts, tone: 'critical' },
    ];
  });
  protected readonly activeFilterSummary = computed(() => {
    const filters = this.filters();
    const tokens: string[] = [];

    if (filters.search.trim()) {
      tokens.push(`Busqueda: ${filters.search.trim()}`);
    }

    if (filters.keyword.trim()) {
      tokens.push(`Keyword: ${filters.keyword.trim()}`);
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

    if (filters.processCategory && filters.processCategory !== 'all') {
      tokens.push(`Tipo: ${this.describeProcessCategory(filters.processCategory)}`);
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

  protected readonly filters = signal<CommercialFilters>(this.createDefaultFilters());

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

  protected readonly newProcessesLane = computed(() =>
    this.opportunities().filter((item) => this.getDaysSincePublication(item) === 0)
  );
  protected readonly currentLane = computed(() =>
    this.opportunities().filter((item) => this.getDaysSincePublication(item) === 1)
  );
  protected readonly staleLane = computed(() =>
    this.opportunities().filter((item) => this.getDaysSincePublication(item) > 1)
  );
  protected readonly statusGroups = computed(() => {
    const groups = new Map<string, OpportunityListItem[]>();
    for (const item of this.opportunities()) {
      const key = item.estado || 'sin_estado';
      const existing = groups.get(key) ?? [];
      existing.push(item);
      groups.set(key, existing);
    }

    return Array.from(groups.entries())
      .map(([label, items]) => ({ label, items: this.sortByPublication(items) }))
      .sort((left, right) => this.getPublicationTimestamp(right.items[0]) - this.getPublicationTimestamp(left.items[0]));
  });

  constructor() {
    void this.initialize();
  }

  private getPublicationTimestamp(item: OpportunityListItem): number {
    return item.fechaPublicacion ? new Date(item.fechaPublicacion).getTime() : 0;
  }

  private sortByPublication(items: OpportunityListItem[]): OpportunityListItem[] {
    return [...items].sort((a, b) => this.getPublicationTimestamp(b) - this.getPublicationTimestamp(a));
  }

  private getDaysSincePublication(item: OpportunityListItem): number {
    const publicationKey = this.getEcuadorDateKey(item.fechaPublicacion);
    const todayKey = this.getEcuadorTodayKey();
    if (!publicationKey || !todayKey) {
      return Number.MAX_SAFE_INTEGER;
    }

    const publicationDate = new Date(`${publicationKey}T00:00:00-05:00`);
    const todayDate = new Date(`${todayKey}T00:00:00-05:00`);
    return Math.max(0, Math.floor((todayDate.getTime() - publicationDate.getTime()) / 86400000));
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

  protected patchFilters<K extends keyof CommercialFilters>(field: K, value: CommercialFilters[K]): void {
    this.filters.update((current) => ({ ...current, [field]: value }));
    this.currentPage.set(1);
  }

  protected async clearFilters(): Promise<void> {
    this.filters.set(this.createDefaultFilters());
    this.currentPage.set(1);
    await this.loadList();
  }

  protected goToFirstPage(): void {
    if (this.currentPage() === 1) {
      return;
    }

    this.currentPage.set(1);
    void this.loadList();
  }

  protected goToPreviousPage(): void {
    const next = Math.max(1, this.currentPage() - 1);
    if (next === this.currentPage()) {
      return;
    }

    this.currentPage.set(next);
    void this.loadList();
  }

  protected goToNextPage(): void {
    const next = Math.min(this.totalPages(), this.currentPage() + 1);
    if (next === this.currentPage()) {
      return;
    }

    this.currentPage.set(next);
    void this.loadList();
  }

  protected setPageSize(value: number | string): void {
    const parsed = Number(value);
    const next = Number.isFinite(parsed) && parsed > 0 ? Math.trunc(parsed) : 25;
    if (next === this.pageSize()) {
      return;
    }

    this.pageSize.set(next);
    this.currentPage.set(1);
    void this.loadList();
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
    await this.loadList();
  }

  protected async selectOpportunity(item: OpportunityListItem, openSercop = false): Promise<void> {
    if (openSercop) {
      this.openProcess(item.url);
    }

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

      if (openSercop) {
        this.scrollDetailIntoView();
      }
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
        entity?: string;
        processCode?: string;
        keyword?: string;
        estado?: string;
        zoneId?: string;
        assignedUserId?: string;
        processCategory?: OpportunityProcessCategory;
        invitedOnly?: boolean;
        todayOnly?: boolean;
        grouping?: CommercialGrouping;
      };

      this.filters.set({
        search: parsed.search ?? '',
        entity: parsed.entity ?? '',
        processCode: parsed.processCode ?? '',
        keyword: parsed.keyword ?? '',
        estado: parsed.estado ?? '',
        zoneId: parsed.zoneId ?? '',
        assignedUserId: parsed.assignedUserId ?? '',
        processCategory: this.normalizeProcessCategoryFilter(parsed.processCategory),
        invitedOnly: parsed.invitedOnly ?? false,
        todayOnly: parsed.todayOnly ?? false,
      });
      this.grouping.set(parsed.grouping ?? 'age');
      this.currentPage.set(1);
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

  protected export(format: 'csv' | 'excel'): void {
    window.open(this.api.exportOpportunitiesUrl({
      format,
      ...this.filters(),
      chemistryOnly: !this.isAllScope(),
      zoneId: this.filters().zoneId ? Number(this.filters().zoneId) : null,
      assignedUserId: this.filters().assignedUserId ? Number(this.filters().assignedUserId) : null,
    }), '_blank');
  }

  protected describeProcessCategory(value: OpportunityProcessCategory): string {
    switch (value) {
      case 'infimas':
        return 'Ínfimas';
      case 'nco':
        return 'Necesidades de contratación';
      case 'sie':
        return 'Subastas inversas';
      case 're':
        return 'Régimen especial';
      case 'other_public':
        return 'Otros procesos públicos';
      default:
        return 'Todos';
    }
  }

  protected async runVisibilityCheck(): Promise<void> {
    const code = this.visibilityCode().trim();
    if (!code) {
      return;
    }

    this.supportVisibility.set(await firstValueFrom(this.api.getOpportunityVisibility(code, this.filters().todayOnly)));
  }

  protected async importVisibilityCode(): Promise<void> {
    if (!this.canManageAssignments()) {
      return;
    }

    const code = this.visibilityCode().trim();
    if (!code) {
      return;
    }

    this.visibilityImporting.set(true);
    try {
      await firstValueFrom(this.api.importOpportunityByCode(code));
      await this.runVisibilityCheck();
      await this.loadList();
    } catch (error) {
      this.errorMessage.set(this.extractError(error));
    } finally {
      this.visibilityImporting.set(false);
    }
  }

  protected openProcess(url: string): void {
    if (!url) {
      return;
    }

    window.open(url, '_blank', 'noopener,noreferrer');
  }

  private scrollDetailIntoView(): void {
    requestAnimationFrame(() => {
      this.detailPanel?.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
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
    const [zones, users, views, sercopStatus] = await Promise.all([
      firstValueFrom(this.api.getZones()),
      firstValueFrom(this.api.getUsers(1, 100)),
      firstValueFrom(this.api.getSavedViews('commercial', 1, 30)),
      firstValueFrom(this.api.getSercopOperationalStatus()),
    ]);

    this.zones.set(zones);
    this.users.set(users.items);
    this.savedViews.set(views.items);
    this.sercopStatus.set(sercopStatus);
  }

  private async loadSavedViews(): Promise<void> {
    const views = await firstValueFrom(this.api.getSavedViews('commercial', 1, 30));
    this.savedViews.set(views.items);
  }

  private async loadList(): Promise<void> {
    const requestId = ++this.listRequestSequence;
    this.loading.set(true);
    this.errorMessage.set('');
    try {
      const filters = this.filters();
      const page = this.currentPage();
      const pageSize = this.pageSize();
      const [items, alerts] = await Promise.all([
        firstValueFrom(this.api.getOpportunities({
          search: filters.search.trim() || undefined,
          entity: filters.entity.trim() || undefined,
          processCode: filters.processCode.trim() || undefined,
          keyword: filters.keyword.trim() || undefined,
          estado: filters.estado || undefined,
          zoneId: filters.zoneId ? Number(filters.zoneId) : null,
          assignedUserId: filters.assignedUserId ? Number(filters.assignedUserId) : null,
          processCategory: filters.processCategory,
          invitedOnly: filters.invitedOnly,
          todayOnly: filters.todayOnly,
          chemistryOnly: !this.isAllScope(),
          page,
          pageSize,
        })),
        firstValueFrom(this.api.getCommercialAlerts()),
      ]);

      if (requestId !== this.listRequestSequence) {
        return;
      }

      const itemsSorted = this.sortByPublication(items.items);
      this.opportunities.set(itemsSorted);
      this.totalCount.set(items.totalCount);
      this.alertSummary.set(alerts);

      const selectedId = this.selectedOpportunityId();
      const selectedStillVisible = selectedId ? itemsSorted.some((item) => item.id === selectedId) : false;
      if (!selectedStillVisible) {
        this.selectedOpportunity.set(null);
        this.activities.set([]);
      }

      if (!this.selectedOpportunity() && itemsSorted.length > 0) {
        await this.selectOpportunity(itemsSorted[0]);
      }
    } catch (error) {
      if (requestId !== this.listRequestSequence) {
        return;
      }

      this.errorMessage.set(this.extractError(error));
    } finally {
      if (requestId === this.listRequestSequence) {
        this.loading.set(false);
      }
    }
  }

  private extractError(error: unknown): string {
    if (error instanceof HttpErrorResponse && typeof error.error?.detail === 'string') {
      return error.error.detail;
    }

    return 'No se pudo completar la operacion.';
  }

  private normalizeProcessCategoryFilter(value: string | null | undefined): OpportunityProcessCategory {
    switch ((value ?? '').trim().toLowerCase()) {
      case 'nco_infimas':
        return 'nco';
      case 'regimen_especial':
        return 're';
      case 'procesos_contratacion':
        return 'other_public';
      case 'infimas':
      case 'nco':
      case 'sie':
      case 're':
      case 'other_public':
        return value as OpportunityProcessCategory;
      default:
        return 'all';
    }
  }

  private createDefaultFilters(): CommercialFilters {
    return {
      search: '',
      entity: '',
      processCode: '',
      keyword: '',
      estado: '',
      zoneId: '',
      assignedUserId: '',
      processCategory: 'all',
      invitedOnly: false,
      todayOnly: false,
    };
  }
}
