# Análisis Técnico: Filtros de Visibilidad en el CRM

## FLUJO DE FILTRADO DE PROCESOS

### 1. CONSULTA SQL BASE (LoadOpportunityRowsAsync)
[Archivo: backend/Data/CrmRepository.OpportunityData.cs, línea 11-115]

```sql
SELECT ...
FROM opportunities o
LEFT JOIN crm_zones z ON z.id = o.zone_id
LEFT JOIN crm_users u ON u.id = o.assigned_user_id
LEFT JOIN LATERAL (activities) ...
LEFT JOIN LATERAL (reminders) ...
WHERE (@estado = '' OR COALESCE(o.estado, '') = @estado)
  AND (@has_zone_filter = FALSE OR o.zone_id = @zone_id)
  AND (@has_assigned_user_filter = FALSE OR o.assigned_user_id = @assigned_user_id)
  AND (@invited_only = FALSE OR o.is_invited_match = TRUE)
  AND (@keyword = '' OR @keyword = ANY(COALESCE(o.keywords_hit, ARRAY[]::text[])))
  AND o.fecha_publicacion >= @retention_cutoff
  /*__DYNAMIC_FILTERS__*/
ORDER BY o.is_invited_match DESC, COALESCE(o.fecha_publicacion, o.created_at) DESC NULLS LAST, o.id DESC;
```

### 2. PARÁMETROS SQL Y VALORES POR DEFECTO

| Parámetro | Por defecto | Efecto |
|-----------|------------|--------|
| `@estado` | '' (vacío) | No filtra por estado |
| `@has_zone_filter` | FALSE | No filtra por zona |
| `@has_assigned_user_filter` | FALSE | No filtra por usuario asignado |
| `@invited_only` | FALSE | Incluye TODOS los procesos (invitados y no invitados) |
| `@keyword` | '' (vacío) | No filtra por palabras clave |
| `@retention_cutoff` | ahora - 5 días | Procesos de últimos 5 días |

### 3. FILTRADO EN MEMORIA (C# - FilterVisibleRows)
[Archivo: backend/Data/CrmRepository.OpportunityData.cs, línea 245-280]

```csharp
private List<VisibleOpportunityRow> FilterVisibleRows(
    IReadOnlyList<OpportunityProjectionRow> rows,
    KeywordRuleSnapshot keywordRules,
    AuthenticatedCrmUser? actor,
    string processCategory,
    bool todayOnly,
    bool chemistryOnly)
{
    var visible = new List<VisibleOpportunityRow>(rows.Count);
    
    foreach (var row in rows)
    {
        // PASO 1: Verificar acceso del usuario
        if (actor is not null && !CanActorAccessOpportunity(actor, row))
            continue;
        
        // PASO 2: Evaluar visibilidad (chemistry y todayOnly)
        var evaluation = EvaluateVisibility(row, keywordRules, todayOnly, chemistryOnly);
        if (!evaluation.IsVisible)
            continue;
        
        // PASO 3: Filtrar por categoría de proceso
        var category = ResolveStoredProcessCategory(row.ProcessCategory, row.Source, row.Tipo, row.ProcessCode);
        if (!OpportunityProcessCategory.MatchesFilter(category, processCategory))
            continue;
        
        visible.Add(new VisibleOpportunityRow(row, BuildDerivedMetrics(row), category));
    }
    
    return visible
        .OrderByDescending(item => item.Row.IsInvitedMatch)
        .ThenByDescending(item => item.Metrics.SortDate)
        .ThenByDescending(item => item.Row.Id)
        .ToList();
}
```

### 4. FUNCIÓN: CanActorAccessOpportunity
[Línea 285-293]

```csharp
private static bool CanActorAccessOpportunity(AuthenticatedCrmUser actor, OpportunityProjectionRow row)
{
    if (!CrmRoleRules.IsSeller(actor))
        return true;  // Admins, gerencia, etc. ven todos
    
    // Si el usuario es VENDEDOR, solo ve procesos asignados a él
    return row.AssignedUserId == actor.Id;
}
```

**Implicación para nuestros procesos:**
- Todos tienen `assigned_user_id = NULL`
- Los vendedores NO los verán
- Los administradores SÍ los verán

### 5. FUNCIÓN: EvaluateVisibility
[Línea 295-318]

```csharp
private OpportunityVisibilityEvaluation EvaluateVisibility(
    OpportunityProjectionRow row,
    KeywordRuleSnapshot keywordRules,
    bool todayOnly,
    bool chemistryOnly)
{
    var reasons = new List<string>();
    
    // FILTRO 1: Filtro de Química
    if (chemistryOnly)
    {
        if (!row.IsChemistryCandidate)
        {
            reasons.Add("No se muestra porque la clasificación persistida lo marcó fuera del módulo químico.");
        }
    }
    
    // FILTRO 2: Filtro de hoy
    if (todayOnly && !IsCurrentDay(row))
    {
        reasons.Add("No se muestra porque el filtro activo solo permite procesos del dia actual en Ecuador.");
    }
    
    return new OpportunityVisibilityEvaluation(reasons.Count == 0, reasons);
}

private static bool IsCurrentDay(OpportunityProjectionRow row)
{
    var today = EcuadorTime.Now().Date;
    return row.FechaPublicacion?.Date == today || row.FechaLimite?.Date == today;
}
```

### 6. ENDPOINTS DEL FRONTEND

#### GET `/api/opportunities`
[Archivo: backend/Endpoints/OpportunityEndpoints.cs, línea 12-67]

```csharp
// Parámetros recibidos:
string? search,
string? entity,
string? processCode,
string? keyword,
string? estado,
long? zoneId,
long? assignedUserId,
string? processCategory,
bool? invitedOnly,
bool? todayOnly,
bool? chemistryOnly,
int? page,
int? pageSize
```

**Lógica importante:**
```csharp
var chemistryScope = chemistryOnly ?? true;  // ⚠️ DEFECTO: true

// Los no-admins DEBEN usar chemistryOnly = true
if (!chemistryScope && !CrmRoleRules.CanAccessCommercialAllScope(actor))
{
    return Results.Forbid();
}
```

**Conclusión**: Por defecto, si no se especifica `chemistryOnly`, se usa `true` (modo química).

#### GET `/api/opportunities/visibility`
[Línea 117-124]

Endpoint para verificar visibilidad de un proceso específico:
```csharp
group.MapGet("/opportunities/visibility", async (
    string code,
    bool? todayOnly,
    CrmRepository repository,
    ...) =>
{
    var visibility = await repository.GetOpportunityVisibilityAsync(
        code, todayOnly ?? false, actor, cancellationToken);
    return Results.Ok(visibility);
});
```

Retorna un objeto `OpportunityVisibilityDto` que indica:
- Si el proceso existe en BD
- Si el usuario tiene acceso
- Todos los motivos por los que no se muestra

## MATRIZ DE VISIBILIDAD

| Proceso | En BD | SQL ✓ | Role ✓ | Chemistry ✓ | Today ✓ | Category ✓ | Result |
|---------|-------|-------|--------|------------|--------|------------|--------|
| NIC-0660001250001-2026-00145 (chem=true) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **VER EN /quimica** |
| NIC-0660001250001-2026-00147 (chem=true) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **VER EN /quimica** |
| NIC-1760003410001-2026-00440 (chem=true) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **VER EN /quimica** |
| NIC-1768092990001-2026-00007 (chem=true) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **VER EN /quimica** |
| NIC-1768153530001-2026-00100 (chem=true) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **VER EN /quimica** |
| NC-1768153530001-2026-00282 (chem=false) | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | **NO EN /quimica, VER EN /todos** |

## POSIBLES RAZONES POR LAS QUE NO SE MUESTRAN

### 1. El usuario es VENDEDOR
```csharp
if (CrmRoleRules.IsSeller(actor))
{
    if (row.AssignedUserId != actor.Id)
        return false;  // No ve procesos sin asignar
}
```
**Nuestros procesos**: Todos tienen `assigned_user_id = NULL` → **Vendedores NO los ven**

### 2. El usuario no tiene acceso a `/commercial/todos`
Solo ven `/commercial/quimica` por defecto.

Roles que acceden a `/commercial/todos`:
- admin
- gerencia
- coordinator
- usuario con loginName = "importaciones"

### 3. El usuario está viendo `/commercial/quimica` ahora
- NC-1768153530001-2026-00282 está excluido porque `is_chemistry_candidate = false`
- Los otros 5 DEBERÍAN aparecer

### 4. Filtro de búsqueda/palabra clave activo
Si se aplicó un filtro de palabra clave que ninguno tiene, se filtraría.

### 5. Filtro de "hoy" activo (todayOnly=true)
NC-1768153530001-2026-00100 fue publicado ayer → se filtraría

## SOLUCIONES

### Para ver los 5 procesos químicos:
1. Ir a `/commercial/quimica` (por defecto)
2. Verificar que no hay filtros activos
3. Verificar que tu usuario NO es vendedor
4. Si eres vendedor, solicitar asignación de procesos

### Para ver el proceso NC-1768153530001-2026-00282:
1. Ir a `/commercial/todos`
2. Verificar que tienes rol de admin, gerencia, coordinator, o "importaciones"
3. Buscar por OCID/NIC específico si es necesario

### Para verificar visibilidad programáticamente:
```bash
# Ver por qué no aparece un proceso
curl "http://localhost:5000/api/opportunities/visibility?code=NIC-0660001250001-2026-00145"

# Respuesta mostrará si existe, si tienes acceso, y todos los motivos de no visibilidad
```
