using System.Net.Mail;
using Npgsql;

namespace backend.Endpoints;

internal static class EndpointValidation
{
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

        if (request.Weight <= 0)
        {
            errors["weight"] = ["El peso debe ser mayor que cero."];
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

        return errors;
    }

    public static async Task<Dictionary<string, string[]>> ValidateUserRequestAsync(UserUpsertRequest request, CrmRepository repository, bool isCreate, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.LoginName))
        {
            errors["loginName"] = ["El login es obligatorio."];
        }

        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            errors["fullName"] = ["El nombre completo es obligatorio."];
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            errors["email"] = ["El correo es obligatorio."];
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

        if (request.ZoneId.HasValue && !await repository.ZoneExistsAsync(request.ZoneId.Value, cancellationToken))
        {
            errors["zoneId"] = ["La zona seleccionada no existe."];
        }

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

        return errors;
    }

    public static Dictionary<string, string[]> ValidateInvitationUpdateRequest(OpportunityInvitationUpdateRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.IsInvitedMatch && string.IsNullOrWhiteSpace(request.InvitationSource))
        {
            errors["invitationSource"] = ["Debes indicar la fuente de la invitacion confirmada."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> ValidateLoginRequest(LoginRequestDto request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Identifier))
        {
            errors["identifier"] = ["Debes indicar correo o login."];
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            errors["password"] = ["La clave es obligatoria."];
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

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["El nombre es obligatorio."];
        }

        if (string.IsNullOrWhiteSpace(request.FiltersJson))
        {
            errors["filtersJson"] = ["Los filtros serializados son obligatorios."];
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

        return errors;
    }

    public static Dictionary<string, string[]> ValidateReminderRequest(OpportunityReminderUpsertRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.RemindAt.HasValue && request.RemindAt.Value < EcuadorTime.Now().AddMinutes(-1))
        {
            errors["remindAt"] = ["La fecha del recordatorio no puede quedar en el pasado."];
        }

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
}
