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

public sealed record OpportunityDocumentDto(
    long Id,
    string SourceUrl,
    string? LocalPath,
    string? MimeType,
    string? Sha256,
    int ChunkCount,
    DateTimeOffset CreatedAt
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
    string? AiResumen,
    string AiRiesgosJson,
    string AiChecklistJson,
    string? AiEstrategiaAbastecimiento,
    string AiListaCotizacionJson,
    string AiPreguntasAbiertasJson,
    string RawPayloadJson,
    IReadOnlyList<OpportunityDocumentDto> Documents,
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

public sealed record InvitationCodeVerificationRequest(
    string CodesText
);

public sealed record InvitationCodeVerificationItemDto(
    string RequestedCode,
    bool ProcessResolved,
    bool IsInvited,
    bool StoredInCrm,
    long? OpportunityId,
    string? ResolvedProcessCode,
    DateTimeOffset? FechaPublicacion,
    string? MatchedSupplierName,
    string? EvidenceUrl,
    string? Notes
);

public sealed record InvitationCodeVerificationResultDto(
    int RequestedCount,
    int VerifiedCount,
    int StoredCount,
    IReadOnlyList<InvitationCodeVerificationItemDto> Items
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

public sealed record AssistantAskRequest(
    string Question,
    string? Module,
    long? OpportunityId,
    string? WorkflowId,
    string? FilePath,
    string? Language,
    string? Selection,
    string? CodeContext
);

public sealed record AssistantSourceDto(
    string Label,
    string Reference,
    string Kind
);

public sealed record AssistantReplyDto(
    string Module,
    string Model,
    string ContextSummary,
    string Answer,
    IReadOnlyList<AssistantSourceDto> Sources
);

public sealed record PersonalAssistantAskRequest(
    string Question,
    long? SessionId,
    string? SearchMode,
    string? FilePath,
    string? Language,
    string? Selection,
    string? CodeContext
);

public sealed record PersonalAssistantUploadedDocumentDto(
    string FileName,
    string ContentType,
    long SizeBytes,
    int CharacterCount,
    string DownloadUrl
);

public sealed record PersonalAssistantReplyDto(
    long SessionId,
    string SessionTitle,
    string SearchMode,
    bool UsedWebSearch,
    int LearnedItems,
    string Model,
    string Answer,
    IReadOnlyList<AssistantSourceDto> Sources,
    IReadOnlyList<PersonalAssistantUploadedDocumentDto> UploadedDocuments
);

public sealed record PersonalAssistantSessionDto(
    long Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount
);

public sealed record PersonalAssistantMessageDto(
    long Id,
    string Role,
    string Content,
    string? Model,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AssistantSourceDto> Sources
);

public sealed record PersonalAssistantSessionDetailDto(
    long Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PersonalAssistantMessageDto> Messages
);

public sealed record PersonalAssistantMemoryDto(
    long Id,
    string MemoryKind,
    string Title,
    string Content,
    string SourceKind,
    string? SourceUrl,
    decimal Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    IReadOnlyList<AssistantSourceDto> Sources
);

public sealed record StudioGenerateRequest(
    string AssetScope,
    long? OpportunityId,
    string? WorkflowId,
    string? Audience,
    string? Tone,
    string? Goal,
    bool IncludeReport,
    bool IncludeVideo,
    bool RenderVideo
);

public sealed record StudioSceneDto(
    int Order,
    string Title,
    string OverlayText,
    string VisualBrief,
    string Voiceover
);

public sealed record StudioAssetDto(
    long Id,
    string AssetType,
    string AssetScope,
    long? OpportunityId,
    string? WorkflowId,
    string Title,
    string Format,
    string? Audience,
    string? Tone,
    string ModelName,
    string? ContentText,
    string PayloadJson,
    DateTimeOffset CreatedAt
);

public sealed record StudioGenerateResultDto(
    string AssetScope,
    string Model,
    string ContextSummary,
    string? ReportMarkdown,
    string? VideoHeadline,
    string? VideoHook,
    string? VideoVoiceover,
    string? StoryboardMarkdown,
    string? CaptionsSrt,
    long? RenderedVideoAssetId,
    IReadOnlyList<StudioSceneDto> Scenes,
    IReadOnlyList<StudioAssetDto> SavedAssets
);
