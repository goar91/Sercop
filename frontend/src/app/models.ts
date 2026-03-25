export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface MetaInfo {
  n8nEditorUrl: string;
  storageMode: string;
  storageTarget: string;
  responsibleEmail?: string | null;
  invitedCompanyName: string;
}

export interface CurrentUser {
  id: number;
  loginName: string;
  fullName: string;
  email: string;
  role: 'admin' | 'gerencia' | 'coordinator' | 'seller' | 'analyst';
  zoneId?: number | null;
  zoneName?: string | null;
  mustChangePassword: boolean;
}

export interface LoginRequest {
  identifier: string;
  password: string;
  rememberMe: boolean;
}

export interface LoginResponse {
  user: CurrentUser;
  message: string;
}

export interface DashboardMetric {
  label: string;
  count: number;
}

export interface ZoneLoad {
  zoneId: number | null;
  zoneName: string;
  count: number;
}

export interface DashboardSummary {
  totalOpportunities: number;
  invitedOpportunities: number;
  assignedOpportunities: number;
  unassignedOpportunities: number;
  activeZones: number;
  activeUsers: number;
  workflowCount: number;
  statuses: DashboardMetric[];
  zoneLoads: ZoneLoad[];
}

export interface CommercialAlertItem {
  opportunityId: number;
  processCode: string;
  titulo: string;
  severity: 'critical' | 'warning' | string;
  message: string;
  referenceAt?: string | null;
}

export interface CommercialAlertSummary {
  totalAlerts: number;
  criticalAlerts: number;
  warningAlerts: number;
  items: CommercialAlertItem[];
}

export interface OpportunityListItem {
  id: number;
  source: string;
  externalId: string;
  ocidOrNic: string;
  processCode: string;
  titulo: string;
  entidad?: string | null;
  tipo?: string | null;
  fechaPublicacion?: string | null;
  fechaLimite?: string | null;
  montoRef?: number | null;
  url: string;
  invitedCompanyName?: string | null;
  isInvitedMatch: boolean;
  invitationSource?: string | null;
  invitationVerifiedAt?: string | null;
  matchScore: number;
  aiScore: number;
  recomendacion?: string | null;
  estado?: string | null;
  resultado?: string | null;
  priority: string;
  zoneName?: string | null;
  assignedUserName?: string | null;
  daysOpen: number;
  agingBucket: string;
  lastActivityAt?: string | null;
  nextActionAt?: string | null;
  hasPendingAction: boolean;
  slaStatus: string;
}

export interface AssignmentHistoryItem {
  id: number;
  assignedUserId?: number | null;
  assignedUserName?: string | null;
  zoneId?: number | null;
  zoneName?: string | null;
  previousStatus?: string | null;
  newStatus?: string | null;
  notes?: string | null;
  changedAt: string;
}

export interface OpportunityReminder {
  id: number;
  remindAt: string;
  notes?: string | null;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  createdAt: string;
  completedAt?: string | null;
}

export interface OpportunityActivity {
  id: number;
  activityType: string;
  body?: string | null;
  metadataJson: string;
  createdByUserId?: number | null;
  createdByUserName?: string | null;
  createdAt: string;
}

export interface OpportunityDetail {
  id: number;
  source: string;
  externalId: string;
  ocidOrNic: string;
  processCode: string;
  titulo: string;
  entidad?: string | null;
  tipo?: string | null;
  fechaPublicacion?: string | null;
  fechaLimite?: string | null;
  montoRef?: number | null;
  moneda: string;
  url: string;
  invitedCompanyName?: string | null;
  isInvitedMatch: boolean;
  invitationSource?: string | null;
  invitationNotes?: string | null;
  invitationEvidenceUrl?: string | null;
  invitationVerifiedAt?: string | null;
  matchScore: number;
  aiScore: number;
  recomendacion?: string | null;
  estado?: string | null;
  vendedor?: string | null;
  resultado?: string | null;
  priority: string;
  crmNotes?: string | null;
  assignmentUpdatedAt?: string | null;
  zoneId?: number | null;
  zoneName?: string | null;
  zoneCode?: string | null;
  assignedUserId?: number | null;
  assignedUserName?: string | null;
  assignedUserEmail?: string | null;
  daysOpen: number;
  agingBucket: string;
  lastActivityAt?: string | null;
  nextActionAt?: string | null;
  hasPendingAction: boolean;
  slaStatus: string;
  reminder?: OpportunityReminder | null;
  assignmentHistory: AssignmentHistoryItem[];
}

export interface OpportunityAssignmentRequest {
  assignedUserId?: number | null;
  zoneId?: number | null;
  estado?: string | null;
  priority?: string | null;
  notes?: string | null;
}

export interface OpportunityActivityCreateRequest {
  activityType: string;
  body?: string | null;
  metadataJson?: string | null;
}

export interface OpportunityReminderUpsertRequest {
  remindAt?: string | null;
  notes?: string | null;
}

export interface OpportunityInvitationUpdateRequest {
  isInvitedMatch: boolean;
  invitationSource?: string | null;
  invitationEvidenceUrl?: string | null;
  invitationNotes?: string | null;
}

export interface OpportunityVisibility {
  processCode: string;
  existsInDatabase: boolean;
  visible: boolean;
  opportunityId?: number | null;
  reasons: string[];
}

export interface BulkInvitationImportRequest {
  codesText: string;
  invitationSource?: string | null;
  invitationEvidenceUrl?: string | null;
  invitationNotes?: string | null;
}

export interface BulkInvitationImportResult {
  requestedCount: number;
  confirmedCount: number;
  updatedCodes: string[];
  unmatchedCodes: string[];
}

export interface InvitationSyncResult {
  scannedCount: number;
  confirmedCount: number;
  updatedCount: number;
  confirmedProcessCodes: string[];
  errors: string[];
}

export interface Zone {
  id: number;
  name: string;
  code: string;
  description?: string | null;
  active: boolean;
}

export interface ZoneUpsertRequest {
  name: string;
  code: string;
  description?: string | null;
  active: boolean;
}

export interface User {
  id: number;
  loginName: string;
  fullName: string;
  email: string;
  role: string;
  phone?: string | null;
  active: boolean;
  zoneId?: number | null;
  zoneName?: string | null;
  mustChangePassword: boolean;
  lastLoginAt?: string | null;
}

export interface UserUpsertRequest {
  loginName: string;
  fullName: string;
  email: string;
  role: string;
  phone?: string | null;
  active: boolean;
  zoneId?: number | null;
  password?: string | null;
  mustChangePassword: boolean;
}

export interface KeywordRule {
  id: number;
  ruleType: 'include' | 'exclude';
  scope: 'all' | 'ocds' | 'nco';
  keyword: string;
  family?: string | null;
  weight: number;
  notes?: string | null;
  active: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface KeywordRuleUpsertRequest {
  ruleType: 'include' | 'exclude';
  scope: 'all' | 'ocds' | 'nco';
  keyword: string;
  family?: string | null;
  weight: number;
  notes?: string | null;
  active: boolean;
}

export interface WorkflowNode {
  id: string;
  name: string;
  type: string;
  x: number;
  y: number;
  disabled: boolean;
}

export interface WorkflowSummary {
  id: string;
  name: string;
  active: boolean;
  description?: string | null;
  updatedAt: string;
  nodeCount: number;
}

export interface WorkflowDetail extends WorkflowSummary {
  nodes: WorkflowNode[];
  connectionsJson: string;
}

export interface SavedView {
  id: number;
  userId: number;
  viewType: string;
  name: string;
  filtersJson: string;
  shared: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface SavedViewUpsertRequest {
  viewType: string;
  name: string;
  filtersJson: string;
  shared: boolean;
}

export interface ManagementSummary {
  range: string;
  totalVisibleOpportunities: number;
  assignedOpportunities: number;
  participatingOpportunities: number;
  wonOpportunities: number;
  lostOpportunities: number;
  notPresentedOpportunities: number;
  activeSellers: number;
  overallHitRatePercent: number;
  totalWonAmount: number;
  salesShareBasis: string;
}

export interface ManagementStageMetric {
  label: string;
  count: number;
}

export interface ManagementSellerPerformance {
  sellerId?: number | null;
  sellerName: string;
  assignedCount: number;
  participatingCount: number;
  wonCount: number;
  lostCount: number;
  notPresentedCount: number;
  salesAmount: number;
  salesSharePercent: number;
  hitRatePercent: number;
  winningAreas: string[];
}

export interface ManagementAreaWin {
  area: string;
  wonCount: number;
}

export interface ManagementZoneMetric {
  zoneId?: number | null;
  zoneName: string;
  assignedCount: number;
  wonCount: number;
  hitRatePercent: number;
}

export interface ManagementAlert {
  code: string;
  label: string;
  count: number;
  severity: string;
}

export interface ManagementAgingBucket {
  bucket: string;
  count: number;
}

export interface ManagementTrendPoint {
  label: string;
  createdCount: number;
  wonCount: number;
}

export interface ManagementReport {
  summary: ManagementSummary;
  pipeline: ManagementStageMetric[];
  sellers: ManagementSellerPerformance[];
  winningAreas: ManagementAreaWin[];
  zoneMetrics: ManagementZoneMetric[];
  alerts: ManagementAlert[];
  aging: ManagementAgingBucket[];
  trend: ManagementTrendPoint[];
}
