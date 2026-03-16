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
}

export interface OpportunityDocument {
  id: number;
  sourceUrl: string;
  localPath?: string | null;
  mimeType?: string | null;
  sha256?: string | null;
  chunkCount: number;
  createdAt: string;
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
  aiResumen?: string | null;
  aiRiesgosJson: string;
  aiChecklistJson: string;
  aiEstrategiaAbastecimiento?: string | null;
  aiListaCotizacionJson: string;
  aiPreguntasAbiertasJson: string;
  rawPayloadJson: string;
  documents: OpportunityDocument[];
  assignmentHistory: AssignmentHistoryItem[];
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
  fullName: string;
  email: string;
  role: string;
  phone?: string | null;
  active: boolean;
  zoneId?: number | null;
  zoneName?: string | null;
}

export interface UserUpsertRequest {
  fullName: string;
  email: string;
  role: string;
  phone?: string | null;
  active: boolean;
  zoneId?: number | null;
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

export interface OpportunityAssignmentRequest {
  assignedUserId?: number | null;
  zoneId?: number | null;
  estado?: string | null;
  priority?: string | null;
  notes?: string | null;
}

export interface OpportunityInvitationUpdateRequest {
  isInvitedMatch: boolean;
  invitationSource?: string | null;
  invitationEvidenceUrl?: string | null;
  invitationNotes?: string | null;
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

export interface MetaInfo {
  n8nEditorUrl: string;
  storageMode: string;
  storageTarget: string;
  responsibleEmail?: string | null;
  invitedCompanyName: string;
}

export interface AssistantAskRequest {
  question: string;
  module?: string | null;
  opportunityId?: number | null;
  workflowId?: string | null;
}

export interface AssistantSource {
  label: string;
  reference: string;
  kind: string;
}

export interface AssistantReply {
  module: string;
  model: string;
  contextSummary: string;
  answer: string;
  sources: AssistantSource[];
}
