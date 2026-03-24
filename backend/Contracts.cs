namespace backend;

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
    string? AssignedUserName
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
    string FullName,
    string Email,
    string Role,
    string? Phone,
    bool Active,
    long? ZoneId,
    string? ZoneName
);

public sealed record UserUpsertRequest(
    string FullName,
    string Email,
    string Role,
    string? Phone,
    bool Active,
    long? ZoneId
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

public sealed record MetaDto(
    string N8nEditorUrl,
    string StorageMode,
    string StorageTarget,
    string? ResponsibleEmail,
    string InvitedCompanyName
);

public sealed record ManagementSummaryDto(
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

public sealed record ManagementReportDto(
    ManagementSummaryDto Summary,
    IReadOnlyList<ManagementStageMetricDto> Pipeline,
    IReadOnlyList<ManagementSellerPerformanceDto> Sellers,
    IReadOnlyList<ManagementAreaWinDto> WinningAreas
);
