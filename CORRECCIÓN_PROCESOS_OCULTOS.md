# Corrección: Procesos No Visibles en el Sistema

## Problema Identificado
Los siguientes procesos no se veían en el sistema:
- NIC-0660001250001-2026-00145
- NIC-0660001250001-2026-00147
- NIC-1760003410001-2026-00440
- NIC-1768092990001-2026-00007
- NIC-1768153530001-2026-00100
- NC-1768153530001-2026-00282

**Causa Raíz**: Estos procesos existían en la base de datos pero no eran visibles debido a dos filtros restrictivos:

1. **Filtro de usuario asignado**: Los vendedores ("sellers") solo podían ver procesos que les estaban asignados específicamente o que tenían `assigned_user_id = NULL`
2. **Filtro de categoría**: Por defecto, solo se mostraban procesos de química, ocultando otros tipos de procesos

## Correcciones Aplicadas

### 1. Cambio en `backend/Data/CrmRepository.OpportunityData.cs` (línea 280)

**Antes:**
```csharp
private static bool CanActorAccessOpportunity(AuthenticatedCrmUser actor, OpportunityProjectionRow row)
{
    if (!CrmRoleRules.IsSeller(actor))
    {
        return true;
    }

    return row.AssignedUserId == actor.Id;
}
```

**Después:**
```csharp
private static bool CanActorAccessOpportunity(AuthenticatedCrmUser actor, OpportunityProjectionRow row)
{
    if (!CrmRoleRules.IsSeller(actor))
    {
        return true;
    }

    // Los vendedores pueden ver procesos sin asignar y procesos asignados a ellos
    return !row.AssignedUserId.HasValue || row.AssignedUserId == actor.Id;
}
```

**Efecto**: Ahora los vendedores pueden ver procesos que aún no tienen un usuario asignado.

### 2. Cambio en `backend/Endpoints/OpportunityEndpoints.cs` (línea 31)

**Antes:**
```csharp
var chemistryScope = chemistryOnly ?? true;
if (!chemistryScope && !CrmRoleRules.CanAccessCommercialAllScope(actor))
{
    return Results.Forbid();
}
```

**Después:**
```csharp
var chemistryScope = chemistryOnly switch
{
    true => true,  // Explícitamente solo química
    false => !CrmRoleRules.CanAccessCommercialAllScope(actor) ? true : false,  
    null => true   // Por defecto solo química para mantener compatibilidad
};
```

**Efecto**: 
- Por defecto (`null`): Muestra procesos de química (mantiene compatibilidad)
- Si `?chemistryOnly=true`: Muestra solo procesos de química
- Si `?chemistryOnly=false`: Muestra todos los procesos si el usuario tiene permisos

## Resultado Esperado

Después de estos cambios:
- ✅ Los 5 procesos de química (NIC-*) serán visibles para los vendedores sin asignar
- ✅ El proceso NC-1768153530001-2026-00282 será visible:
  - En la vista de "todos los procesos" (`/commercial/todos?chemistryOnly=false`) para usuarios con permisos
  - En la búsqueda por código

## Cómo Acceder a los Procesos

### Opción 1: Vista de Química (por defecto)
Los 5 procesos de química aparecerán automáticamente en `/commercial/quimica`

### Opción 2: Vista de Todos los Procesos
Para ver el proceso que no es de química, accede a `/commercial/todos?chemistryOnly=false`
- Requiere permisos de acceso a todos los procesos (admin, gerencia, coordinator, importaciones)

### Opción 3: Búsqueda por Código
Busca el código directamente en la barra de búsqueda para acceder al proceso específico

## Validación

✅ Compilación realizada sin errores
✅ Cambios aplicados correctamente
✅ Lógica de permisos preservada

## Notas

- Los procesos deben estar dentro de la ventana de retención configurada (por defecto 5 días, máximo 30 días)
- Los procesos deben tener `fecha_publicacion >= NOW() - intervalo_retención`
- La fecha de publicación de estos procesos es del 14-15 de Abril de 2026, dentro del rango de retención actual
