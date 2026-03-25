import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  BulkInvitationImportRequest,
  BulkInvitationImportResult,
  CommercialAlertSummary,
  CurrentUser,
  DashboardSummary,
  InvitationSyncResult,
  KeywordRule,
  KeywordRuleUpsertRequest,
  LoginRequest,
  LoginResponse,
  ManagementReport,
  MetaInfo,
  OpportunityActivity,
  OpportunityActivityCreateRequest,
  OpportunityAssignmentRequest,
  OpportunityDetail,
  OpportunityInvitationUpdateRequest,
  OpportunityListItem,
  OpportunityReminder,
  OpportunityReminderUpsertRequest,
  OpportunityVisibility,
  PagedResult,
  SavedView,
  SavedViewUpsertRequest,
  User,
  UserUpsertRequest,
  WorkflowDetail,
  WorkflowSummary,
  Zone,
  ZoneUpsertRequest,
} from './models';

@Injectable({ providedIn: 'root' })
export class CrmApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  login(payload: LoginRequest) {
    return this.http.post<LoginResponse>(`${this.baseUrl}/auth/login`, payload);
  }

  logout() {
    return this.http.post<{ message: string }>(`${this.baseUrl}/auth/logout`, {});
  }

  getCurrentUser() {
    return this.http.get<CurrentUser>(`${this.baseUrl}/auth/me`);
  }

  getMeta() {
    return this.http.get<MetaInfo>(`${this.baseUrl}/meta`);
  }

  getDashboard() {
    return this.http.get<DashboardSummary>(`${this.baseUrl}/dashboard`);
  }

  getCommercialAlerts() {
    return this.http.get<CommercialAlertSummary>(`${this.baseUrl}/commercial/alerts`);
  }

  getManagementReport(filters: { range?: string; zoneId?: number | null; sellerId?: number | null }) {
    let params = new HttpParams();

    if (filters.range) {
      params = params.set('range', filters.range);
    }

    if (filters.zoneId) {
      params = params.set('zoneId', filters.zoneId);
    }

    if (filters.sellerId) {
      params = params.set('sellerId', filters.sellerId);
    }

    return this.http.get<ManagementReport>(`${this.baseUrl}/management/report`, { params });
  }

  getOpportunities(filters: {
    search?: string;
    estado?: string;
    zoneId?: number | null;
    assignedUserId?: number | null;
    invitedOnly?: boolean;
    todayOnly?: boolean;
    page?: number;
    pageSize?: number;
  }) {
    let params = new HttpParams();

    if (filters.search) {
      params = params.set('search', filters.search);
    }

    if (filters.estado) {
      params = params.set('estado', filters.estado);
    }

    if (filters.zoneId) {
      params = params.set('zoneId', filters.zoneId);
    }

    if (filters.assignedUserId) {
      params = params.set('assignedUserId', filters.assignedUserId);
    }

    params = params.set('invitedOnly', filters.invitedOnly ?? false);
    params = params.set('todayOnly', filters.todayOnly ?? false);
    params = params.set('page', filters.page ?? 1);
    params = params.set('pageSize', filters.pageSize ?? 25);

    return this.http.get<PagedResult<OpportunityListItem>>(`${this.baseUrl}/opportunities`, { params });
  }

  exportOpportunitiesUrl(filters: {
    format: 'csv' | 'excel';
    search?: string;
    estado?: string;
    zoneId?: number | null;
    assignedUserId?: number | null;
    invitedOnly?: boolean;
    todayOnly?: boolean;
  }) {
    let params = new HttpParams().set('format', filters.format);

    if (filters.search) {
      params = params.set('search', filters.search);
    }

    if (filters.estado) {
      params = params.set('estado', filters.estado);
    }

    if (filters.zoneId) {
      params = params.set('zoneId', filters.zoneId);
    }

    if (filters.assignedUserId) {
      params = params.set('assignedUserId', filters.assignedUserId);
    }

    params = params.set('invitedOnly', filters.invitedOnly ?? false);
    params = params.set('todayOnly', filters.todayOnly ?? false);
    return `${this.baseUrl}/opportunities/export?${params.toString()}`;
  }

  getOpportunity(id: number) {
    return this.http.get<OpportunityDetail>(`${this.baseUrl}/opportunities/${id}`);
  }

  getOpportunityActivities(id: number, page = 1, pageSize = 20) {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<OpportunityActivity>>(`${this.baseUrl}/opportunities/${id}/activities`, { params });
  }

  createOpportunityActivity(id: number, payload: OpportunityActivityCreateRequest) {
    return this.http.post<OpportunityActivity>(`${this.baseUrl}/opportunities/${id}/activities`, payload);
  }

  updateAssignment(id: number, payload: OpportunityAssignmentRequest) {
    return this.http.put<OpportunityDetail>(`${this.baseUrl}/opportunities/${id}/assignment`, payload);
  }

  updateInvitation(id: number, payload: OpportunityInvitationUpdateRequest) {
    return this.http.put<OpportunityDetail>(`${this.baseUrl}/opportunities/${id}/invitation`, payload);
  }

  upsertReminder(id: number, payload: OpportunityReminderUpsertRequest) {
    return this.http.put<OpportunityReminder | null>(`${this.baseUrl}/opportunities/${id}/reminder`, payload);
  }

  getOpportunityVisibility(code: string, todayOnly = false) {
    const params = new HttpParams().set('code', code).set('todayOnly', todayOnly);
    return this.http.get<OpportunityVisibility>(`${this.baseUrl}/opportunities/visibility`, { params });
  }

  importInvitations(payload: BulkInvitationImportRequest) {
    return this.http.post<BulkInvitationImportResult>(`${this.baseUrl}/invitations/import`, payload);
  }

  syncInvitations() {
    return this.http.post<InvitationSyncResult>(`${this.baseUrl}/invitations/sync`, {});
  }

  getZones() {
    return this.http.get<Zone[]>(`${this.baseUrl}/zones`);
  }

  createZone(payload: ZoneUpsertRequest) {
    return this.http.post<Zone>(`${this.baseUrl}/zones`, payload);
  }

  updateZone(id: number, payload: ZoneUpsertRequest) {
    return this.http.put<Zone>(`${this.baseUrl}/zones/${id}`, payload);
  }

  getUsers(page = 1, pageSize = 25) {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<User>>(`${this.baseUrl}/users`, { params });
  }

  createUser(payload: UserUpsertRequest) {
    return this.http.post<User>(`${this.baseUrl}/users`, payload);
  }

  updateUser(id: number, payload: UserUpsertRequest) {
    return this.http.put<User>(`${this.baseUrl}/users/${id}`, payload);
  }

  getKeywordRules(filters: {
    ruleType?: 'include' | 'exclude';
    scope?: 'all' | 'ocds' | 'nco';
    page?: number;
    pageSize?: number;
  }) {
    let params = new HttpParams().set('page', filters.page ?? 1).set('pageSize', filters.pageSize ?? 25);

    if (filters.ruleType) {
      params = params.set('ruleType', filters.ruleType);
    }

    if (filters.scope) {
      params = params.set('scope', filters.scope);
    }

    return this.http.get<PagedResult<KeywordRule>>(`${this.baseUrl}/keywords`, { params });
  }

  createKeywordRule(payload: KeywordRuleUpsertRequest) {
    return this.http.post<KeywordRule>(`${this.baseUrl}/keywords`, payload);
  }

  updateKeywordRule(id: number, payload: KeywordRuleUpsertRequest) {
    return this.http.put<KeywordRule>(`${this.baseUrl}/keywords/${id}`, payload);
  }

  deleteKeywordRule(id: number) {
    return this.http.delete<void>(`${this.baseUrl}/keywords/${id}`);
  }

  getWorkflows(page = 1, pageSize = 25) {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<WorkflowSummary>>(`${this.baseUrl}/workflows`, { params });
  }

  getWorkflow(id: string) {
    return this.http.get<WorkflowDetail>(`${this.baseUrl}/workflows/${id}`);
  }

  getSavedViews(viewType = 'commercial', page = 1, pageSize = 20) {
    const params = new HttpParams().set('viewType', viewType).set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<SavedView>>(`${this.baseUrl}/commercial/views`, { params });
  }

  createSavedView(payload: SavedViewUpsertRequest) {
    return this.http.post<SavedView>(`${this.baseUrl}/commercial/views`, payload);
  }

  updateSavedView(id: number, payload: SavedViewUpsertRequest) {
    return this.http.put<SavedView>(`${this.baseUrl}/commercial/views/${id}`, payload);
  }

  deleteSavedView(id: number) {
    return this.http.delete<void>(`${this.baseUrl}/commercial/views/${id}`);
  }
}
