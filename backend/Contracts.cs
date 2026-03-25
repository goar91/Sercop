namespace backend;

public sealed record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize
);

public sealed record MetaDto(
    string N8nEditorUrl,
    string StorageMode,
    string StorageTarget,
    string? ResponsibleEmail,
    string InvitedCompanyName
);

public sealed record CurrentUserDto(
    long Id,
    string LoginName,
    string FullName,
    string Email,
    string Role,
    long? ZoneId,
    string? ZoneName,
    bool MustChangePassword
);

public sealed record LoginRequestDto(
    string Identifier,
    string Password,
    bool RememberMe
);

public sealed record LoginResponseDto(
    CurrentUserDto User,
    string Message
);

public sealed record DashboardMetricDto(string Label, int Count);

public sealed record ZoneLoadDto(long? ZoneId, string ZoneName, int Count);

public sealed record DashboardSummaryDto(
    int TotalOpportunities,
    int InvitedOpportunities,
    int AssignedOpportunities,
    int UnassignedOpportunities,
    int ActiveZones,
    int ActiveUsers,
    int WorkflowCount,
    IReadOnlyList<DashboardMetricDto> Statuses,
    IReadOnlyList<ZoneLoadDto> ZoneLoads
);

public sealed record CommercialAlertItemDto(
    long OpportunityId,
    string ProcessCode,
    string Titulo,
    string Severity,
    string Message,
    DateTimeOffset? ReferenceAt
);

public sealed record CommercialAlertSummaryDto(
    int TotalAlerts,
    int CriticalAlerts,
    int WarningAlerts,
    IReadOnlyList<CommercialAlertItemDto> Items
);

public sealed record OpportunityListItemDto(
    long Id,
    string Source,
    string ExternalId,
    string OcidOrNic,
    string ProcessCode,
    string Titulo,
    string? Entidad,
    string? Tipo,
    DateTimeOffset? FechaPublicacion,
    DateTimeOffset? FechaLimite,
    decimal? MontoRef,
    string Url,
    string? InvitedCompanyName,
    bool IsInvitedMatch,
    string? InvitationSource,
    DateTimeOffset? InvitationVerifiedAt,
    decimal MatchScore,
    decimal AiScore,
    string? Recomendacion,
    string? Estado,
    string? Resultado,
    string Priority,
    string? ZoneName,
    string? AssignedUserName,
    int DaysOpen,
    string AgingBucket,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? NextActionAt,
    bool HasPendingAction,
    string SlaStatus
);

public sealed record AssignmentHistoryItemDto(
    long Id,
    long? AssignedUserId,
    string? AssignedUserName,
    long? ZoneId,
    string? ZoneName,
    string? PreviousStatus,
    string? NewStatus,
    string? Notes,
    DateTimeOffset ChangedAt
);

public sealed record OpportunityReminderDto(
    long Id,
    DateTimeOffset RemindAt,
    string? Notes,
    long? CreatedByUserId,
    string? CreatedByUserName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt
);

public sealed record OpportunityActivityDto(
    long Id,
    string ActivityType,
    string? Body,
    string MetadataJson,
    long? CreatedByUserId,
    string? CreatedByUserName,
    DateTimeOffset CreatedAt
);

public sealed record OpportunityDetailDto(
    long Id,
    string Source,
    string ExternalId,
    string OcidOrNic,
    string ProcessCode,
    string Titulo,
    string? Entidad,
    string? Tipo,
    DateTimeOffset? FechaPublicacion,
    DateTimeOffset? FechaLimite,
    decimal? MontoRef,
    string Moneda,
    string Url,
    string? InvitedCompanyName,
    bool IsInvitedMatch,
    string? InvitationSource,
    string? InvitationNotes,
    string? InvitationEvidenceUrl,
    DateTimeOffset? InvitationVerifiedAt,
    decimal MatchScore,
    decimal AiScore,
    string? Recomendacion,
    string? Estado,
    string? Vendedor,
    string? Resultado,
    string Priority,
    string? CrmNotes,
    DateTimeOffset? AssignmentUpdatedAt,
    long? ZoneId,
    string? ZoneName,
    string? ZoneCode,
    long? AssignedUserId,
    string? AssignedUserName,
    string? AssignedUserEmail,
    int DaysOpen,
    string AgingBucket,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? NextActionAt,
    bool HasPendingAction,
    string SlaStatus,
    OpportunityReminderDto? Reminder,
    IReadOnlyList<AssignmentHistoryItemDto> AssignmentHistory
);

public sealed record OpportunityAssignmentRequest(
    long? AssignedUserId,
    long? ZoneId,
    string? Estado,
    string? Priority,
    string? Notes
);

public sealed record OpportunityInvitationUpdateRequest(
    bool IsInvitedMatch,
    string? InvitationSource,
    string? InvitationEvidenceUrl,
    string? InvitationNotes
);

public sealed record OpportunityActivityCreateRequest(
    string ActivityType,
    string? Body,
    string? MetadataJson
);

public sealed record OpportunityReminderUpsertRequest(
    DateTimeOffset? RemindAt,
    string? Notes
);

public sealed record OpportunityVisibilityDto(
    string ProcessCode,
    bool ExistsInDatabase,
    bool Visible,
    long? OpportunityId,
    IReadOnlyList<string> Reasons
);

public sealed record ImportOpportunityByCodeRequest(
    string Code
);

public sealed record BulkInvitationImportRequest(
    string CodesText,
    string? InvitationSource,
    string? InvitationEvidenceUrl,
    string? InvitationNotes
);

public sealed record BulkInvitationImportResultDto(
    int RequestedCount,
    int ConfirmedCount,
    IReadOnlyList<string> UpdatedCodes,
    IReadOnlyList<string> UnmatchedCodes
);

public sealed record InvitationSyncResultDto(
    int ScannedCount,
    int ConfirmedCount,
    int UpdatedCount,
    IReadOnlyList<string> ConfirmedProcessCodes,
    IReadOnlyList<string> Errors
);

public sealed record ZoneDto(long Id, string Name, string Code, string? Description, bool Active);

public sealed record ZoneUpsertRequest(string Name, string Code, string? Description, bool Active);

public sealed record UserDto(
    long Id,
    string LoginName,
    string FullName,
    string Email,
    string Role,
    string? Phone,
    bool Active,
    long? ZoneId,
    string? ZoneName,
    bool MustChangePassword,
    DateTimeOffset? LastLoginAt
);

public sealed record UserUpsertRequest(
    string LoginName,
    string FullName,
    string Email,
    string Role,
    string? Phone,
    bool Active,
    long? ZoneId,
    string? Password,
    bool MustChangePassword
);

public sealed record KeywordRuleDto(
    long Id,
    string RuleType,
    string Scope,
    string Keyword,
    string? Family,
    decimal Weight,
    string? Notes,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record KeywordRuleUpsertRequest(
    string RuleType,
    string Scope,
    string Keyword,
    string? Family,
    decimal Weight,
    string? Notes,
    bool Active
);

public sealed record WorkflowNodeDto(
    string Id,
    string Name,
    string Type,
    double X,
    double Y,
    bool Disabled
);

public sealed record WorkflowSummaryDto(
    string Id,
    string Name,
    bool Active,
    string? Description,
    DateTimeOffset UpdatedAt,
    int NodeCount
);

public sealed record WorkflowDetailDto(
    string Id,
    string Name,
    bool Active,
    string? Description,
    DateTimeOffset UpdatedAt,
    int NodeCount,
    IReadOnlyList<WorkflowNodeDto> Nodes,
    string ConnectionsJson
);

public sealed record SavedViewDto(
    long Id,
    long UserId,
    string ViewType,
    string Name,
    string FiltersJson,
    bool Shared,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record SavedViewUpsertRequest(
    string ViewType,
    string Name,
    string FiltersJson,
    bool Shared
);

public sealed record ManagementSummaryDto(
    string Range,
    int TotalVisibleOpportunities,
    int AssignedOpportunities,
    int ParticipatingOpportunities,
    int WonOpportunities,
    int LostOpportunities,
    int NotPresentedOpportunities,
    int ActiveSellers,
    decimal OverallHitRatePercent,
    decimal TotalWonAmount,
    string SalesShareBasis
);

public sealed record ManagementStageMetricDto(
    string Label,
    int Count
);

public sealed record ManagementSellerPerformanceDto(
    long? SellerId,
    string SellerName,
    int AssignedCount,
    int ParticipatingCount,
    int WonCount,
    int LostCount,
    int NotPresentedCount,
    decimal SalesAmount,
    decimal SalesSharePercent,
    decimal HitRatePercent,
    IReadOnlyList<string> WinningAreas
);

public sealed record ManagementAreaWinDto(
    string Area,
    int WonCount
);

public sealed record ManagementZoneMetricDto(
    long? ZoneId,
    string ZoneName,
    int AssignedCount,
    int WonCount,
    decimal HitRatePercent
);

public sealed record ManagementAlertDto(
    string Code,
    string Label,
    int Count,
    string Severity
);

public sealed record ManagementAgingBucketDto(
    string Bucket,
    int Count
);

public sealed record ManagementTrendPointDto(
    string Label,
    int CreatedCount,
    int WonCount
);

public sealed record ManagementReportDto(
    ManagementSummaryDto Summary,
    IReadOnlyList<ManagementStageMetricDto> Pipeline,
    IReadOnlyList<ManagementSellerPerformanceDto> Sellers,
    IReadOnlyList<ManagementAreaWinDto> WinningAreas,
    IReadOnlyList<ManagementZoneMetricDto> ZoneMetrics,
    IReadOnlyList<ManagementAlertDto> Alerts,
    IReadOnlyList<ManagementAgingBucketDto> Aging,
    IReadOnlyList<ManagementTrendPointDto> Trend
);
