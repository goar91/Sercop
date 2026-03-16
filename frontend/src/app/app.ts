import { HttpErrorResponse } from '@angular/common/http';
import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CrmApiService } from './crm-api.service';
import {
  BulkInvitationImportResult,
  DashboardSummary,
  InvitationSyncResult,
  KeywordRule,
  MetaInfo,
  OpportunityDetail,
  OpportunityListItem,
  User,
  WorkflowDetail,
  WorkflowSummary,
  Zone,
} from './models';

@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  private readonly api = inject(CrmApiService);
  private readonly opportunityFiltersStorageKey = 'sercop-crm-opportunity-filters';

  protected readonly meta = signal<MetaInfo | null>(null);
  protected readonly dashboard = signal<DashboardSummary | null>(null);
  protected readonly opportunities = signal<OpportunityListItem[]>([]);
  protected readonly selectedOpportunityId = signal<number | null>(null);
  protected readonly selectedOpportunity = signal<OpportunityDetail | null>(null);
  protected readonly zones = signal<Zone[]>([]);
  protected readonly users = signal<User[]>([]);
  protected readonly keywordRules = signal<KeywordRule[]>([]);
  protected readonly workflowSummaries = signal<WorkflowSummary[]>([]);
  protected readonly selectedWorkflowId = signal<string | null>(null);
  protected readonly selectedWorkflow = signal<WorkflowDetail | null>(null);
  protected readonly filteredKeywordRules = computed(() => {
    const search = this.keywordListFilters.search.trim().toLowerCase();
    const type = this.keywordListFilters.ruleType;
    const scope = this.keywordListFilters.scope;
    const status = this.keywordListFilters.status;

    return this.keywordRules().filter((rule) => {
      const matchesSearch = !search
        || rule.keyword.toLowerCase().includes(search)
        || (rule.family ?? '').toLowerCase().includes(search)
        || (rule.notes ?? '').toLowerCase().includes(search);
      const matchesType = !type || rule.ruleType === type;
      const matchesScope = !scope || rule.scope === scope;
      const matchesStatus = status === 'all'
        || (status === 'active' && rule.active)
        || (status === 'inactive' && !rule.active);

      return matchesSearch && matchesType && matchesScope && matchesStatus;
    });
  });

  protected readonly formattedOpportunity = computed(() => {
    const detail = this.selectedOpportunity();
    if (!detail) {
      return null;
    }

    return {
      aiRiesgos: this.formatJson(detail.aiRiesgosJson),
      aiChecklist: this.formatJson(detail.aiChecklistJson),
      aiListaCotizacion: this.formatJson(detail.aiListaCotizacionJson),
      aiPreguntasAbiertas: this.formatJson(detail.aiPreguntasAbiertasJson),
      rawPayload: this.formatJson(detail.rawPayloadJson),
    };
  });

  protected readonly workflowLayout = computed(() => {
    const workflow = this.selectedWorkflow();
    if (!workflow || workflow.nodes.length === 0) {
      return {
        width: '100%',
        height: '320px',
        nodeStyles: {} as Record<string, { left: string; top: string }>,
      };
    }

    const xs = workflow.nodes.map((node) => node.x);
    const ys = workflow.nodes.map((node) => node.y);
    const minX = Math.min(...xs);
    const maxX = Math.max(...xs);
    const minY = Math.min(...ys);
    const maxY = Math.max(...ys);
    const nodeStyles = Object.fromEntries(
      workflow.nodes.map((node) => [
        node.id,
        {
          left: `${node.x - minX + 32}px`,
          top: `${node.y - minY + 32}px`,
        },
      ]),
    );

    return {
      width: `${maxX - minX + 360}px`,
      height: `${maxY - minY + 220}px`,
      nodeStyles,
    };
  });

  protected readonly loading = signal(false);
  protected readonly detailLoading = signal(false);
  protected readonly workflowLoading = signal(false);
  protected readonly savingAssignment = signal(false);
  protected readonly savingInvitation = signal(false);
  protected readonly importingInvitations = signal(false);
  protected readonly syncingInvitations = signal(false);
  protected readonly savingZone = signal(false);
  protected readonly savingUser = signal(false);
  protected readonly savingKeyword = signal(false);
  protected readonly errorMessage = signal('');
  protected readonly successMessage = signal('');
  protected readonly invitationImportResult = signal<BulkInvitationImportResult | null>(null);
  protected readonly invitationSyncResult = signal<InvitationSyncResult | null>(null);

  protected readonly statusOptions = ['nuevo', 'en_revision', 'asignado', 'presentado', 'ganado', 'perdido', 'no_presentado'];
  protected readonly priorityOptions = ['alta', 'normal', 'baja'];
  protected readonly roleOptions = ['manager', 'seller', 'analyst'];
  protected readonly keywordRuleTypeOptions = ['include', 'exclude'] as const;
  protected readonly keywordScopeOptions = ['all', 'ocds', 'nco'] as const;

  protected filters = this.loadOpportunityFilters();

  protected assignmentForm = {
    assignedUserId: '',
    zoneId: '',
    estado: 'asignado',
    priority: 'normal',
    notes: '',
  };

  protected invitationForm = {
    isInvitedMatch: false,
    invitationSource: 'reporte_sercop',
    invitationEvidenceUrl: '',
    invitationNotes: '',
  };

  protected zoneForm = {
    id: null as number | null,
    name: '',
    code: '',
    description: '',
    active: true,
  };

  protected userForm = {
    id: null as number | null,
    fullName: '',
    email: '',
    role: 'seller',
    phone: '',
    active: true,
    zoneId: '',
  };

  protected keywordForm = {
    id: null as number | null,
    ruleType: 'include' as 'include' | 'exclude',
    scope: 'all' as 'all' | 'ocds' | 'nco',
    keyword: '',
    family: '',
    weight: 1,
    notes: '',
    active: true,
  };

  protected invitationImportForm = {
    codesText: '',
    invitationSource: 'reporte_sercop',
    invitationEvidenceUrl: '',
    invitationNotes: '',
  };

  protected keywordListFilters = {
    search: '',
    ruleType: '' as '' | 'include' | 'exclude',
    scope: '' as '' | 'all' | 'ocds' | 'nco',
    status: 'all' as 'all' | 'active' | 'inactive',
  };

  constructor() {
    void this.reloadAll();
  }

  protected async reloadAll(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');
    this.invitationImportResult.set(null);
    this.invitationSyncResult.set(null);

    try {
      const [meta, dashboard, zones, users, keywordRules, workflowSummaries, opportunities] = await Promise.all([
        firstValueFrom(this.api.getMeta()),
        firstValueFrom(this.api.getDashboard()),
        firstValueFrom(this.api.getZones()),
        firstValueFrom(this.api.getUsers()),
        firstValueFrom(this.api.getKeywordRules()),
        firstValueFrom(this.api.getWorkflows()),
        firstValueFrom(this.api.getOpportunities(this.getFiltersPayload())),
      ]);

      this.meta.set(meta);
      this.dashboard.set(dashboard);
      this.zones.set(zones);
      this.users.set(users);
      this.keywordRules.set(keywordRules);
      this.workflowSummaries.set(workflowSummaries);
      this.opportunities.set(opportunities);

      await this.ensureSelectedWorkflow(workflowSummaries);
      await this.syncOpportunitySelection(opportunities, false);
    } catch (error) {
      this.handleError(error, 'No se pudo cargar el CRM.');
    } finally {
      this.loading.set(false);
    }
  }

  protected async applyFilters(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set('');

    try {
      this.saveOpportunityFilters();
      const opportunities = await firstValueFrom(this.api.getOpportunities(this.getFiltersPayload()));
      this.opportunities.set(opportunities);
      await this.syncOpportunitySelection(opportunities, false);
    } catch (error) {
      this.handleError(error, 'No se pudieron aplicar los filtros.');
    } finally {
      this.loading.set(false);
    }
  }

  protected clearFilters(): void {
    this.filters = this.createDefaultOpportunityFilters();
    this.saveOpportunityFilters();

    void this.applyFilters();
  }

  protected async selectOpportunity(id: number, showLoading = true): Promise<void> {
    this.selectedOpportunityId.set(id);
    if (showLoading) {
      this.detailLoading.set(true);
    }

    try {
      const detail = await firstValueFrom(this.api.getOpportunity(id));
      this.selectedOpportunity.set(detail);
      this.assignmentForm = {
        assignedUserId: detail.assignedUserId ? String(detail.assignedUserId) : '',
        zoneId: detail.zoneId ? String(detail.zoneId) : '',
        estado: detail.estado ?? 'asignado',
        priority: detail.priority ?? 'normal',
        notes: detail.crmNotes ?? '',
      };
      this.invitationForm = {
        isInvitedMatch: detail.isInvitedMatch,
        invitationSource: detail.invitationSource ?? 'reporte_sercop',
        invitationEvidenceUrl: detail.invitationEvidenceUrl ?? '',
        invitationNotes: detail.invitationNotes ?? '',
      };
    } catch (error) {
      this.handleError(error, 'No se pudo cargar el detalle del proceso.');
    } finally {
      if (showLoading) {
        this.detailLoading.set(false);
      }
    }
  }

  protected async saveAssignment(): Promise<void> {
    const opportunityId = this.selectedOpportunityId();
    if (!opportunityId) {
      return;
    }

    this.savingAssignment.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    try {
      const detail = await firstValueFrom(this.api.updateAssignment(opportunityId, {
        assignedUserId: this.assignmentForm.assignedUserId ? Number(this.assignmentForm.assignedUserId) : null,
        zoneId: this.assignmentForm.zoneId ? Number(this.assignmentForm.zoneId) : null,
        estado: this.assignmentForm.estado || null,
        priority: this.assignmentForm.priority || null,
        notes: this.assignmentForm.notes || null,
      }));

      this.selectedOpportunity.set(detail);
      this.successMessage.set('Asignacion actualizada correctamente.');
      await this.refreshSnapshot();
    } catch (error) {
      this.handleError(error, 'No se pudo guardar la asignacion.');
    } finally {
      this.savingAssignment.set(false);
    }
  }

  protected async saveInvitation(): Promise<void> {
    const opportunityId = this.selectedOpportunityId();
    if (!opportunityId) {
      return;
    }

    this.savingInvitation.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    try {
      const detail = await firstValueFrom(this.api.updateInvitation(opportunityId, {
        isInvitedMatch: this.invitationForm.isInvitedMatch,
        invitationSource: this.invitationForm.isInvitedMatch ? (this.invitationForm.invitationSource.trim() || null) : null,
        invitationEvidenceUrl: this.invitationForm.isInvitedMatch ? (this.invitationForm.invitationEvidenceUrl.trim() || null) : null,
        invitationNotes: this.invitationForm.isInvitedMatch ? (this.invitationForm.invitationNotes.trim() || null) : null,
      }));

      this.selectedOpportunity.set(detail);
      this.invitationForm = {
        isInvitedMatch: detail.isInvitedMatch,
        invitationSource: detail.invitationSource ?? 'reporte_sercop',
        invitationEvidenceUrl: detail.invitationEvidenceUrl ?? '',
        invitationNotes: detail.invitationNotes ?? '',
      };
      this.successMessage.set('Estado de invitacion actualizado correctamente.');
      await this.refreshSnapshot();
    } catch (error) {
      this.handleError(error, 'No se pudo guardar la invitacion.');
    } finally {
      this.savingInvitation.set(false);
    }
  }

  protected async importInvitations(): Promise<void> {
    this.importingInvitations.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');
    this.invitationImportResult.set(null);

    try {
      const result = await firstValueFrom(this.api.importInvitations({
        codesText: this.invitationImportForm.codesText,
        invitationSource: this.invitationImportForm.invitationSource.trim() || null,
        invitationEvidenceUrl: this.invitationImportForm.invitationEvidenceUrl.trim() || null,
        invitationNotes: this.invitationImportForm.invitationNotes.trim() || null,
      }));

      this.invitationImportResult.set(result);
      this.successMessage.set(`Importacion completada. ${result.confirmedCount} procesos confirmados.`);
      this.invitationImportForm = {
        ...this.invitationImportForm,
        codesText: '',
      };
      await this.refreshSnapshot();
    } catch (error) {
      this.handleError(error, 'No se pudo importar la lista de invitaciones.');
    } finally {
      this.importingInvitations.set(false);
    }
  }

  protected async syncInvitations(): Promise<void> {
    this.syncingInvitations.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');
    this.invitationSyncResult.set(null);

    try {
      const result = await firstValueFrom(this.api.syncInvitations());
      this.invitationSyncResult.set(result);
      this.successMessage.set(`Sincronizacion completada. ${result.confirmedCount} procesos confirmados para HDM.`);
      await this.refreshSnapshot();
    } catch (error) {
      this.handleError(error, 'No se pudo sincronizar el reporte publico de invitaciones.');
    } finally {
      this.syncingInvitations.set(false);
    }
  }

  protected async saveZone(): Promise<void> {
    this.savingZone.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    try {
      const payload = {
        name: this.zoneForm.name.trim(),
        code: this.zoneForm.code.trim().toUpperCase(),
        description: this.zoneForm.description.trim() || null,
        active: this.zoneForm.active,
      };

      if (this.zoneForm.id) {
        await firstValueFrom(this.api.updateZone(this.zoneForm.id, payload));
      } else {
        await firstValueFrom(this.api.createZone(payload));
      }

      this.resetZoneForm();
      this.successMessage.set('Zona guardada correctamente.');
      await this.refreshReferenceData();
    } catch (error) {
      this.handleError(error, 'No se pudo guardar la zona.');
    } finally {
      this.savingZone.set(false);
    }
  }

  protected editZone(zone: Zone): void {
    this.zoneForm = {
      id: zone.id,
      name: zone.name,
      code: zone.code,
      description: zone.description ?? '',
      active: zone.active,
    };
  }

  protected resetZoneForm(): void {
    this.zoneForm = {
      id: null,
      name: '',
      code: '',
      description: '',
      active: true,
    };
  }

  protected async saveUser(): Promise<void> {
    this.savingUser.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    try {
      const payload = {
        fullName: this.userForm.fullName.trim(),
        email: this.userForm.email.trim().toLowerCase(),
        role: this.userForm.role,
        phone: this.userForm.phone.trim() || null,
        active: this.userForm.active,
        zoneId: this.userForm.zoneId ? Number(this.userForm.zoneId) : null,
      };

      if (this.userForm.id) {
        await firstValueFrom(this.api.updateUser(this.userForm.id, payload));
      } else {
        await firstValueFrom(this.api.createUser(payload));
      }

      this.resetUserForm();
      this.successMessage.set('Usuario guardado correctamente.');
      await this.refreshReferenceData();
    } catch (error) {
      this.handleError(error, 'No se pudo guardar el usuario.');
    } finally {
      this.savingUser.set(false);
    }
  }

  protected editUser(user: User): void {
    this.userForm = {
      id: user.id,
      fullName: user.fullName,
      email: user.email,
      role: user.role,
      phone: user.phone ?? '',
      active: user.active,
      zoneId: user.zoneId ? String(user.zoneId) : '',
    };
  }

  protected resetUserForm(): void {
    this.userForm = {
      id: null,
      fullName: '',
      email: '',
      role: 'seller',
      phone: '',
      active: true,
      zoneId: '',
    };
  }

  protected async saveKeyword(): Promise<void> {
    this.savingKeyword.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    try {
      const payload = {
        ruleType: this.keywordForm.ruleType,
        scope: this.keywordForm.scope,
        keyword: this.keywordForm.keyword.trim().toLowerCase(),
        family: this.keywordForm.family.trim() || null,
        weight: Number(this.keywordForm.weight) || 1,
        notes: this.keywordForm.notes.trim() || null,
        active: this.keywordForm.active,
      };

      if (this.keywordForm.id) {
        await firstValueFrom(this.api.updateKeywordRule(this.keywordForm.id, payload));
      } else {
        await firstValueFrom(this.api.createKeywordRule(payload));
      }

      this.resetKeywordForm();
      this.successMessage.set('Palabra clave guardada correctamente.');
      await this.refreshReferenceData();
    } catch (error) {
      this.handleError(error, 'No se pudo guardar la palabra clave.');
    } finally {
      this.savingKeyword.set(false);
    }
  }

  protected editKeyword(rule: KeywordRule): void {
    this.keywordForm = {
      id: rule.id,
      ruleType: rule.ruleType,
      scope: rule.scope,
      keyword: rule.keyword,
      family: rule.family ?? '',
      weight: rule.weight,
      notes: rule.notes ?? '',
      active: rule.active,
    };
  }

  protected resetKeywordForm(): void {
    this.keywordForm = {
      id: null,
      ruleType: 'include',
      scope: 'all',
      keyword: '',
      family: '',
      weight: 1,
      notes: '',
      active: true,
    };
  }

  protected clearKeywordListFilters(): void {
    this.keywordListFilters = {
      search: '',
      ruleType: '',
      scope: '',
      status: 'all',
    };
  }

  protected async selectWorkflow(id: string): Promise<void> {
    this.selectedWorkflowId.set(id);
    await this.loadWorkflowDetail(id);
  }

  protected openN8n(): void {
    const url = this.meta()?.n8nEditorUrl;
    if (url) {
      window.open(url, '_blank', 'noopener');
    }
  }

  private async ensureSelectedWorkflow(workflowSummaries: WorkflowSummary[]): Promise<void> {
    const selectedId = this.selectedWorkflowId();
    const workflowId = selectedId && workflowSummaries.some((workflow) => workflow.id === selectedId)
      ? selectedId
      : workflowSummaries[0]?.id ?? null;

    this.selectedWorkflowId.set(workflowId);

    if (workflowId) {
      await this.loadWorkflowDetail(workflowId);
    } else {
      this.selectedWorkflow.set(null);
    }
  }

  private async loadWorkflowDetail(id: string): Promise<void> {
    this.workflowLoading.set(true);

    try {
      const workflow = await firstValueFrom(this.api.getWorkflow(id));
      this.selectedWorkflow.set(workflow);
    } catch (error) {
      this.handleError(error, 'No se pudo cargar el workflow seleccionado.');
    } finally {
      this.workflowLoading.set(false);
    }
  }

  private async syncOpportunitySelection(opportunities: OpportunityListItem[], showLoadingForDetail: boolean): Promise<void> {
    const selectedId = this.selectedOpportunityId();
    const nextOpportunityId = selectedId && opportunities.some((item) => item.id === selectedId)
      ? selectedId
      : opportunities[0]?.id ?? null;

    if (nextOpportunityId) {
      await this.selectOpportunity(nextOpportunityId, showLoadingForDetail);
      return;
    }

    this.selectedOpportunityId.set(null);
    this.selectedOpportunity.set(null);
  }

  private async refreshReferenceData(): Promise<void> {
    const [zones, users, keywordRules, dashboard] = await Promise.all([
      firstValueFrom(this.api.getZones()),
      firstValueFrom(this.api.getUsers()),
      firstValueFrom(this.api.getKeywordRules()),
      firstValueFrom(this.api.getDashboard()),
    ]);

    this.zones.set(zones);
    this.users.set(users);
    this.keywordRules.set(keywordRules);
    this.dashboard.set(dashboard);
  }

  private async refreshSnapshot(): Promise<void> {
    const [dashboard, opportunities] = await Promise.all([
      firstValueFrom(this.api.getDashboard()),
      firstValueFrom(this.api.getOpportunities(this.getFiltersPayload())),
    ]);

    this.dashboard.set(dashboard);
    this.opportunities.set(opportunities);
    await this.syncOpportunitySelection(opportunities, false);
  }

  private getFiltersPayload(): {
    search?: string;
    estado?: string;
    zoneId?: number | null;
    assignedUserId?: number | null;
    invitedOnly: boolean;
  } {
    return {
      search: this.filters.search.trim() || undefined,
      estado: this.filters.estado || undefined,
      zoneId: this.filters.zoneId ? Number(this.filters.zoneId) : null,
      assignedUserId: this.filters.assignedUserId ? Number(this.filters.assignedUserId) : null,
      invitedOnly: this.filters.invitedOnly,
    };
  }

  private createDefaultOpportunityFilters(): {
    search: string;
    estado: string;
    zoneId: string;
    assignedUserId: string;
    invitedOnly: boolean;
  } {
    return {
      search: '',
      estado: '',
      zoneId: '',
      assignedUserId: '',
      invitedOnly: false,
    };
  }

  private loadOpportunityFilters(): {
    search: string;
    estado: string;
    zoneId: string;
    assignedUserId: string;
    invitedOnly: boolean;
  } {
    const fallback = this.createDefaultOpportunityFilters();

    try {
      const raw = globalThis.localStorage?.getItem(this.opportunityFiltersStorageKey);
      if (!raw) {
        return fallback;
      }

      const parsed = JSON.parse(raw);
      return {
        search: typeof parsed.search === 'string' ? parsed.search : fallback.search,
        estado: typeof parsed.estado === 'string' ? parsed.estado : fallback.estado,
        zoneId: typeof parsed.zoneId === 'string' ? parsed.zoneId : fallback.zoneId,
        assignedUserId: typeof parsed.assignedUserId === 'string' ? parsed.assignedUserId : fallback.assignedUserId,
        invitedOnly: typeof parsed.invitedOnly === 'boolean' ? parsed.invitedOnly : fallback.invitedOnly,
      };
    } catch {
      return fallback;
    }
  }

  private saveOpportunityFilters(): void {
    try {
      globalThis.localStorage?.setItem(this.opportunityFiltersStorageKey, JSON.stringify(this.filters));
    } catch {
      // Ignore storage failures and keep filters in memory.
    }
  }

  private formatJson(value: string | null | undefined): string {
    if (!value) {
      return '';
    }

    try {
      return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      return value;
    }
  }

  private handleError(error: unknown, fallback: string): void {
    console.error(error);
    this.errorMessage.set(this.resolveErrorMessage(error, fallback));
  }

  private resolveErrorMessage(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      if (typeof error.error?.detail === 'string') {
        return error.error.detail;
      }

      if (typeof error.error?.title === 'string') {
        return error.error.title;
      }

      if (error.message) {
        return error.message;
      }
    }

    return fallback;
  }
}
