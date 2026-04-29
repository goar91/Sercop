using System.Net.Mail;
using System.Text.Json;
using Npgsql;

namespace backend.Endpoints;

internal static class EndpointValidation
{
    private const int MaxKeywordLength = 160;
    private const int MaxKeywordFamilyLength = 120;
    private const int MaxKeywordNotesLength = 1000;
    private const int MaxZoneNameLength = 120;
    private const int MaxZoneDescriptionLength = 500;
    private const int MaxLoginLength = 80;
    private const int MaxFullNameLength = 160;
    private const int MaxEmailLength = 200;
    private const int MaxPhoneLength = 40;
    private const int MaxPasswordLength = 200;
    private const int MaxAssignmentNotesLength = 4000;
    private const int MaxInvitationSourceLength = 120;
    private const int MaxInvitationNotesLength = 4000;
    private const int MaxUrlLength = 600;
    private const int MaxSavedViewNameLength = 120;
    private const int MaxSavedViewJsonLength = 4000;
    private const int MaxActivityBodyLength = 4000;
    private const int MaxMetadataJsonLength = 4000;
    private const int MaxReminderNotesLength = 2000;
    private const int MaxCodesTextLength = 12000;
    private const int MaxSercopUserLength = 200;
    private const int MaxSercopRucLength = 20;

    public static Dictionary<string, string[]> ValidateKeywordRuleFilters(string? ruleType, string? scope)
    {
        var errors = new Dictionary<string, string[]>();

        if (!string.IsNullOrWhiteSpace(ruleType) && ruleType is not ("include" or "exclude"))
        {
            errors["ruleType"] = ["Debe ser include o exclude."];
        }

        if (!string.IsNullOrWhiteSpace(scope) && scope is not ("all" or "ocds" or "nco"))
        {
            errors["scope"] = ["Debe ser all, ocds o nco."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateKeywordRuleRequest(KeywordRuleUpsertRequest request)
    {
        var errors = ValidateKeywordRuleFilters(request.RuleType, request.Scope);

        if (string.IsNullOrWhiteSpace(request.Keyword))
        {
            errors["keyword"] = ["La palabra clave es obligatoria."];
        }
        else if (request.Keyword.Trim().Length > MaxKeywordLength)
        {
            errors["keyword"] = [$"La palabra clave no puede superar {MaxKeywordLength} caracteres."];
        }

        if (request.Weight <= 0)
        {
            errors["weight"] = ["El peso debe ser mayor que cero."];
        }

        ValidateMaxLength(errors, "family", request.Family, MaxKeywordFamilyLength, "La familia");
        ValidateMaxLength(errors, "notes", request.Notes, MaxKeywordNotesLength, "Las notas");

        return errors;
    }

    public static Dictionary<string, string[]> ValidateSercopCredentialsRequest(SercopCredentialsUpsertRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Ruc))
        {
            errors["ruc"] = ["El RUC SERCOP es obligatorio."];
        }
        else if (request.Ruc.Trim().Length > MaxSercopRucLength)
        {
            errors["ruc"] = [$"El RUC SERCOP no puede superar {MaxSercopRucLength} caracteres."];
        }

        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            errors["userName"] = ["El usuario SERCOP es obligatorio."];
        }
        else if (request.UserName.Trim().Length > MaxSercopUserLength)
        {
            errors["userName"] = [$"El usuario SERCOP no puede superar {MaxSercopUserLength} caracteres."];
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            errors["password"] = ["La clave SERCOP es obligatoria."];
        }
        else if (request.Password.Length > MaxPasswordLength)
        {
            errors["password"] = [$"La clave SERCOP no puede superar {MaxPasswordLength} caracteres."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateOpportunityFilters(string? processCategory, string? keyword)
    {
        var errors = new Dictionary<string, string[]>();

        if (!OpportunityProcessCategory.IsValidFilter(processCategory))
        {
            errors["processCategory"] = ["Debe ser all, infimas, nco, sie, re, other_public o un alias legado soportado."];
        }

        if (!string.IsNullOrWhiteSpace(keyword) && keyword.Trim().Length > MaxKeywordLength)
        {
            errors["keyword"] = [$"La palabra clave no puede superar {MaxKeywordLength} caracteres."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateZoneRequest(ZoneUpsertRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["El nombre de la zona es obligatorio."];
        }
        else if (request.Name.Trim().Length > MaxZoneNameLength)
        {
            errors["name"] = [$"El nombre no puede superar {MaxZoneNameLength} caracteres."];
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            errors["code"] = ["El codigo de la zona es obligatorio."];
        }
        else
        {
            var normalizedCode = request.Code.Trim().ToUpperInvariant();
            if (normalizedCode.Length is < 2 or > 12)
            {
                errors["code"] = ["El codigo debe tener entre 2 y 12 caracteres."];
            }
        }

        ValidateMaxLength(errors, "description", request.Description, MaxZoneDescriptionLength, "La descripcion");

        return errors;
    }

    public static async Task<Dictionary<string, string[]>> ValidateUserRequestAsync(UserUpsertRequest request, CrmRepository repository, bool isCreate, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.LoginName))
        {
            errors["loginName"] = ["El login es obligatorio."];
        }
        else if (request.LoginName.Trim().Length > MaxLoginLength)
        {
            errors["loginName"] = [$"El login no puede superar {MaxLoginLength} caracteres."];
        }

        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            errors["fullName"] = ["El nombre completo es obligatorio."];
        }
        else if (request.FullName.Trim().Length > MaxFullNameLength)
        {
            errors["fullName"] = [$"El nombre completo no puede superar {MaxFullNameLength} caracteres."];
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            errors["email"] = ["El correo es obligatorio."];
        }
        else if (request.Email.Trim().Length > MaxEmailLength)
        {
            errors["email"] = [$"El correo no puede superar {MaxEmailLength} caracteres."];
        }
        else if (!IsValidEmail(request.Email))
        {
            errors["email"] = ["El correo no tiene un formato valido."];
        }

        var normalizedRole = request.Role?.Trim().ToLowerInvariant();
        if (normalizedRole is not ("admin" or "gerencia" or "coordinator" or "seller" or "analyst"))
        {
            errors["role"] = ["El rol debe ser admin, gerencia, coordinator, seller o analyst."];
        }

        if (isCreate && string.IsNullOrWhiteSpace(request.Password))
        {
            errors["password"] = ["La clave es obligatoria al crear un usuario."];
        }
        else if (!string.IsNullOrWhiteSpace(request.Password) && request.Password.Trim().Length > MaxPasswordLength)
        {
            errors["password"] = [$"La clave no puede superar {MaxPasswordLength} caracteres."];
        }

        if (request.ZoneId.HasValue && !await repository.ZoneExistsAsync(request.ZoneId.Value, cancellationToken))
        {
            errors["zoneId"] = ["La zona seleccionada no existe."];
        }

        ValidateMaxLength(errors, "phone", request.Phone, MaxPhoneLength, "El telefono");

        return errors;
    }

    public static async Task<Dictionary<string, string[]>> ValidateAssignmentRequestAsync(OpportunityAssignmentRequest request, CrmRepository repository, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (!string.IsNullOrWhiteSpace(request.Estado))
        {
            var normalizedStatus = request.Estado.Trim().ToLowerInvariant();
            if (normalizedStatus is not ("nuevo" or "en_revision" or "asignado" or "presentado" or "ganado" or "perdido" or "no_presentado"))
            {
                errors["estado"] = ["El estado no es valido."];
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Priority))
        {
            var normalizedPriority = request.Priority.Trim().ToLowerInvariant();
            if (normalizedPriority is not ("alta" or "normal" or "baja"))
            {
                errors["priority"] = ["La prioridad debe ser alta, normal o baja."];
            }
        }

        if (request.AssignedUserId.HasValue && !await repository.UserExistsAsync(request.AssignedUserId.Value, cancellationToken))
        {
            errors["assignedUserId"] = ["El vendedor seleccionado no existe."];
        }

        if (request.ZoneId.HasValue && !await repository.ZoneExistsAsync(request.ZoneId.Value, cancellationToken))
        {
            errors["zoneId"] = ["La zona seleccionada no existe."];
        }

        ValidateMaxLength(errors, "notes", request.Notes, MaxAssignmentNotesLength, "Las notas");

        return errors;
    }

    public static Dictionary<string, string[]> ValidateInvitationUpdateRequest(OpportunityInvitationUpdateRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.IsInvitedMatch && string.IsNullOrWhiteSpace(request.InvitationSource))
        {
            errors["invitationSource"] = ["Debes indicar la fuente de la invitacion confirmada."];
        }

        ValidateMaxLength(errors, "invitationSource", request.InvitationSource, MaxInvitationSourceLength, "La fuente");
        ValidateMaxLength(errors, "invitationNotes", request.InvitationNotes, MaxInvitationNotesLength, "Las notas");
        ValidateHttpUrl(errors, "invitationEvidenceUrl", request.InvitationEvidenceUrl);

        return errors;
    }

    public static Dictionary<string, string[]> ValidateLoginRequest(LoginRequestDto request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Identifier))
        {
            errors["identifier"] = ["Debes indicar correo o login."];
        }
        else if (request.Identifier.Trim().Length > MaxEmailLength)
        {
            errors["identifier"] = [$"El identificador no puede superar {MaxEmailLength} caracteres."];
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            errors["password"] = ["La clave es obligatoria."];
        }
        else if (request.Password.Length > MaxPasswordLength)
        {
            errors["password"] = [$"La clave no puede superar {MaxPasswordLength} caracteres."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateSavedViewRequest(SavedViewUpsertRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.ViewType))
        {
            errors["viewType"] = ["El tipo de vista es obligatorio."];
        }
        else if (!string.Equals(request.ViewType.Trim(), "commercial", StringComparison.OrdinalIgnoreCase))
        {
            errors["viewType"] = ["El unico tipo de vista soportado actualmente es commercial."];
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["El nombre es obligatorio."];
        }
        else if (request.Name.Trim().Length > MaxSavedViewNameLength)
        {
            errors["name"] = [$"El nombre no puede superar {MaxSavedViewNameLength} caracteres."];
        }

        if (string.IsNullOrWhiteSpace(request.FiltersJson))
        {
            errors["filtersJson"] = ["Los filtros serializados son obligatorios."];
        }
        else
        {
            ValidateJsonObject(errors, "filtersJson", request.FiltersJson, MaxSavedViewJsonLength, "Los filtros serializados");
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateActivityRequest(OpportunityActivityCreateRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedType = request.ActivityType?.Trim().ToLowerInvariant();

        if (normalizedType is not ("note" or "assignment" or "status_change" or "invitation_confirmation" or "reminder" or "system"))
        {
            errors["activityType"] = ["El tipo de actividad no es valido."];
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            errors["body"] = ["Debes registrar el contenido de la actividad."];
        }
        else if (request.Body.Trim().Length > MaxActivityBodyLength)
        {
            errors["body"] = [$"El contenido no puede superar {MaxActivityBodyLength} caracteres."];
        }

        if (!string.IsNullOrWhiteSpace(request.MetadataJson))
        {
            ValidateJsonObject(errors, "metadataJson", request.MetadataJson, MaxMetadataJsonLength, "Los metadatos");
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateReminderRequest(OpportunityReminderUpsertRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.RemindAt.HasValue && request.RemindAt.Value < EcuadorTime.Now().AddMinutes(-1))
        {
            errors["remindAt"] = ["La fecha del recordatorio no puede quedar en el pasado."];
        }

        ValidateMaxLength(errors, "notes", request.Notes, MaxReminderNotesLength, "Las notas");

        return errors;
    }

    public static Dictionary<string, string[]> ValidateBulkInvitationImportRequest(BulkInvitationImportRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.CodesText))
        {
            errors["codesText"] = ["Debes ingresar al menos un codigo de proceso."];
        }
        else if (request.CodesText.Trim().Length > MaxCodesTextLength)
        {
            errors["codesText"] = [$"El listado no puede superar {MaxCodesTextLength} caracteres."];
        }

        ValidateMaxLength(errors, "invitationSource", request.InvitationSource, MaxInvitationSourceLength, "La fuente");
        ValidateMaxLength(errors, "invitationNotes", request.InvitationNotes, MaxInvitationNotesLength, "Las notas");
        ValidateHttpUrl(errors, "invitationEvidenceUrl", request.InvitationEvidenceUrl);

        return errors;
    }

    public static (int StatusCode, string Title, string Detail) MapPostgresException(PostgresException exception)
    {
        return exception.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => (
                StatusCodes.Status409Conflict,
                "Conflicto de datos",
                "Ya existe un registro con esos valores unicos. Revisa correo, login, nombre o codigo."),
            PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.StringDataRightTruncation => (
                StatusCodes.Status400BadRequest,
                "Datos invalidos",
                "La base de datos rechazo la operacion por valores incompletos o fuera de formato."),
            PostgresErrorCodes.ForeignKeyViolation => (
                StatusCodes.Status400BadRequest,
                "Relacion invalida",
                "La operacion referencia una zona, usuario o proceso que no existe."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Error interno del CRM",
                "La solicitud no se pudo completar. Revisa el backend o los datos de PostgreSQL."),
        };
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            _ = new MailAddress(email.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateMaxLength(Dictionary<string, string[]> errors, string key, string? value, int maxLength, string label)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length > maxLength)
        {
            errors[key] = [$"{label} no puede superar {maxLength} caracteres."];
        }
    }

    private static void ValidateHttpUrl(Dictionary<string, string[]> errors, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxUrlLength)
        {
            errors[key] = [$"La URL no puede superar {MaxUrlLength} caracteres."];
            return;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors[key] = ["La URL debe ser absoluta y usar http o https."];
        }
    }

    private static void ValidateJsonObject(Dictionary<string, string[]> errors, string key, string rawValue, int maxLength, string label)
    {
        var trimmed = rawValue.Trim();
        if (trimmed.Length > maxLength)
        {
            errors[key] = [$"{label} no pueden superar {maxLength} caracteres."];
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors[key] = [$"{label} deben ser un objeto JSON valido."];
            }
        }
        catch (JsonException)
        {
            errors[key] = [$"{label} deben ser un objeto JSON valido."];
        }
    }
}
