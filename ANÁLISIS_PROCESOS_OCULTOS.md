# Análisis: Procesos Ocultos en CRM

**Fecha de análisis**: 16 de abril, 2026

## 1. ESTADO DE LOS PROCESOS EN BD

Los **6 procesos existen en PostgreSQL** y están completamente registrados:

| OCID/NIC | Publicación | Chemistry | Scope | Estado | Visible Donde |
|----------|------------|-----------|-------|--------|--------------|
| NIC-0660001250001-2026-00145 | 2026-04-15 08:00 | ✅ TRUE | infimas | nuevo | /todos + /quimica |
| NIC-0660001250001-2026-00147 | 2026-04-15 15:01 | ✅ TRUE | infimas | nuevo | /todos + /quimica |
| NIC-1760003410001-2026-00440 | 2026-04-15 10:04 | ✅ TRUE | infimas | nuevo | /todos + /quimica |
| NIC-1768092990001-2026-00007 | 2026-04-15 11:00 | ✅ TRUE | infimas | nuevo | /todos + /quimica |
| NIC-1768153530001-2026-00100 | 2026-04-14 18:00 | ✅ TRUE | infimas | nuevo | /todos + /quimica |
| NC-1768153530001-2026-00282 | 2026-04-15 16:00 | ❌ FALSE | nco | nuevo | /todos SOLO |

## 2. UBICACIÓN ESPERADA EN INTERFAZ

### Vista `/commercial/quimica` (Módulo Químico)
- **Acceso**: Todos los usuarios
- **Parámetro**: `chemistryOnly = true`
- **Procesos que deberían aparecer**: Los 5 con `is_chemistry_candidate = true`
  - ✅ NIC-0660001250001-2026-00145
  - ✅ NIC-0660001250001-2026-00147
  - ✅ NIC-1760003410001-2026-00440
  - ✅ NIC-1768092990001-2026-00007
  - ✅ NIC-1768153530001-2026-00100

### Vista `/commercial/todos` (Todos los Procesos)
- **Acceso**: Solo admin, gerencia, coordinator, usuario "importaciones"
- **Parámetro**: `chemistryOnly = false`
- **Procesos que deberían aparecer**: Todos los 6

## 3. FILTROS APLICADOS EN EL CÓDIGO

### En Backend (CrmRepository.OpportunityData.cs)
```sql
AND (@invited_only = FALSE OR o.is_invited_match = TRUE)
AND (@keyword = '' OR @keyword = ANY(COALESCE(o.keywords_hit, ARRAY[]::text[])))
AND o.fecha_publicacion >= @retention_cutoff  -- Últimos 5 días (por defecto)
```

**Estado de estos procesos respecto a filtros:**
- ✅ `invitedOnly = false` → Todos pasan (sin filtro)
- ✅ `fecha_publicacion` → Están dentro de últimos 5 días
- ❌ `keywords` → NO tienen palabras clave (`keywords_hit` está vacío para la mayoría)

### En C# (FilterVisibleRows / EvaluateVisibility)
```csharp
if (chemistryOnly)
{
    if (!row.IsChemistryCandidate)
    {
        reasons.Add("No se muestra porque la clasificación persistida lo marcó fuera del módulo químico.");
    }
}
```

## 4. PROBLEMA POTENCIAL IDENTIFICADO

### ⚠️ FILTRO DE PALABRAS CLAVE

Los procesos tienen diferentes estados en `keywords_hit`:

- **Con palabras clave** (capturados en búsquedas):
  - NIC-0660001250001-2026-00145: `{"recoleccion de muestras", "muestras"}`
  - NIC-0660001250001-2026-00147: `{"sustancias quimicas", "laboratorio", "biomateriales", "quimica"}`
  - NIC-1760003410001-2026-00440: `{"laboratorio de alimentos", "microbiologico", "microbiologicos", "laboratorio"}`
  - NIC-1768092990001-2026-00007: `{"muestras biologicas", "muestras"}`
  - NIC-1768153530001-2026-00100: `{"materiales de referencia", "estandares", "control"}`

- **Sin palabras clave**:
  - NC-1768153530001-2026-00282: `{}` (ARRAY VACÍO)

### Implicación
Si el usuario aplica un **filtro de palabra clave específica** en la UI, el proceso NC-1768153530001-2026-00282 se filtrarían porque no tiene palabras clave asignadas.

## 5. RECOMENDACIONES

### Para ver los procesos EN LA INTERFAZ:

1. **Los 5 procesos químicos**:
   - Ir a `/commercial/quimica` (por defecto)
   - O en `/commercial/todos` si tienes permisos
   - **Sin aplicar** filtros de palabra clave

2. **El proceso NC-1768153530001-2026-00282**:
   - Solo visible en `/commercial/todos` (requiere permisos especiales)
   - No aparecerá en `/commercial/quimica` porque `is_chemistry_candidate = false`

### Verificación Manual del Endpoint:
```bash
GET /api/opportunities/visibility?code=NIC-0660001250001-2026-00145&todayOnly=false
```

Este endpoint te dirá exactamente por qué está o no visible un proceso específico.

## 6. DATOS TÉCNICOS ADICIONALES

### Configuración de Retención
- Variable de entorno: `CRM_RETENTION_DAYS` (no configurada)
- Por defecto: 5 días
- Los procesos están dentro del rango de retención ✅

### Campos de Clasificación
```javascript
recomendacion (opinion del AI):
- "revisar" para 5 procesos
- "descartar" para 1 proceso

classification_payload (JSON con info de IA):
- Contiene análisis detallado del proceso
```

### Roles que ven `/commercial/todos`
- admin
- gerencia  
- coordinator
- usuario "importaciones"

## 7. CONCLUSIÓN

**Los 6 procesos SÍ existen en la BD y cumplen todos los criterios para mostrarse:**
- ✅ Están dentro de la ventana de retención
- ✅ Tienen clasificación correcta
- ✅ Pasan todos los filtros SQL
- ✅ El estado es 'nuevo'

**La visibilidad depende de:**
1. **Rol del usuario** para acceder a `/commercial/todos`
2. **La vista elegida** (/quimica vs /todos)
3. **Filtros activos** (especialmente palabras clave)
4. **Permiso de acceso** si hay  asignación por zona/usuario

**Acción recomendada:**
Verificar qué vista está usando el usuario y qué filtros tiene aplicados.
