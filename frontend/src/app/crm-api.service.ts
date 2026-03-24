import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  BulkInvitationImportRequest,
  BulkInvitationImportResult,
  DashboardSummary,
  InvitationSyncResult,
  KeywordRule,
  KeywordRuleUpsertRequest,
  MetaInfo,
  OpportunityAssignmentRequest,
  OpportunityDetail,
  OpportunityInvitationUpdateRequest,
  OpportunityListItem,
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

  getMeta() {
    return this.http.get<MetaInfo>(`${this.baseUrl}/meta`);
  }

  getDashboard() {
    return this.http.get<DashboardSummary>(`${this.baseUrl}/dashboard`);
  }

  getOpportunities(filters: {
    search?: string;
    estado?: string;
    zoneId?: number | null;
    assignedUserId?: number | null;
    invitedOnly?: boolean;
    todayOnly?: boolean;
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
    params = params.set('todayOnly', filters.todayOnly ?? true);
    return this.http.get<OpportunityListItem[]>(`${this.baseUrl}/opportunities`, { params });
  }

  getOpportunity(id: number) {
    return this.http.get<OpportunityDetail>(`${this.baseUrl}/opportunities/${id}`);
  }

  updateAssignment(id: number, payload: OpportunityAssignmentRequest) {
    return this.http.put<OpportunityDetail>(`${this.baseUrl}/opportunities/${id}/assignment`, payload);
  }

  updateInvitation(id: number, payload: OpportunityInvitationUpdateRequest) {
    return this.http.put<OpportunityDetail>(`${this.baseUrl}/opportunities/${id}/invitation`, payload);
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

  getUsers() {
    return this.http.get<User[]>(`${this.baseUrl}/users`);
  }

  createUser(payload: UserUpsertRequest) {
    return this.http.post<User>(`${this.baseUrl}/users`, payload);
  }

  updateUser(id: number, payload: UserUpsertRequest) {
    return this.http.put<User>(`${this.baseUrl}/users/${id}`, payload);
  }

  getKeywordRules(filters?: {
    ruleType?: 'include' | 'exclude';
    scope?: 'all' | 'ocds' | 'nco';
  }) {
    let params = new HttpParams();

    if (filters?.ruleType) {
      params = params.set('ruleType', filters.ruleType);
    }

    if (filters?.scope) {
      params = params.set('scope', filters.scope);
    }

    return this.http.get<KeywordRule[]>(`${this.baseUrl}/keywords`, { params });
  }

  createKeywordRule(payload: KeywordRuleUpsertRequest) {
    return this.http.post<KeywordRule>(`${this.baseUrl}/keywords`, payload);
  }

  updateKeywordRule(id: number, payload: KeywordRuleUpsertRequest) {
    return this.http.put<KeywordRule>(`${this.baseUrl}/keywords/${id}`, payload);
  }

  getWorkflows() {
    return this.http.get<WorkflowSummary[]>(`${this.baseUrl}/workflows`);
  }

  getWorkflow(id: string) {
    return this.http.get<WorkflowDetail>(`${this.baseUrl}/workflows/${id}`);
  }
}
