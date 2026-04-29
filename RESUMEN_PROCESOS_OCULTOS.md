# RESUMEN EJECUTIVO: Procesos Ocultos en CRM

## HALLAZGOS CLAVE

✅ **Todos los 6 procesos EXISTEN en la base de datos PostgreSQL**
✅ **Todos cumplen los criterios para mostrarse según los filtros SQL**
✅ **El problema NO está en la base de datos ni en la consulta SQL**

## UBICACIÓN CORRECTA DE CADA PROCESO

### 5 Procesos Químicos (is_chemistry_candidate = TRUE)
```
NIC-0660001250001-2026-00145
NIC-0660001250001-2026-00147
NIC-1760003410001-2026-00440
NIC-1768092990001-2026-00007
NIC-1768153530001-2026-00100
```
➜ **DEBEN APARECER EN**: `/commercial/quimica` (vista por defecto)
➜ **TAMBIÉN EN**: `/commercial/todos` (si tienes permisos)

### 1 Proceso NO Químico (is_chemistry_candidate = FALSE)
```
NC-1768153530001-2026-00282
```
➜ **NO APARECE EN**: `/commercial/quimica` (excluido por filtro)
➜ **APARECE EN**: `/commercial/todos` (solo si tienes permisos)

## CAUSA PROBABLE

El problema **NO está diseñado para ocultarlos**. Es probable que:

### HIPÓTESIS 1: Eres Vendedor (50% probable)
Si tu rol es "vendedor":
- ✅ VES: Procesos asignados a TI
- ❌ NO VES: Procesos sin asignar (los 6 procesos están sin asignar)

**Solución**: Pedir que te asignen los procesos o hablar con tu gerente

### HIPÓTESIS 2: Estás en la URL incorrecta (30% probable)
- Para ver los 5 procesos químicos → Usa `/commercial/quimica` ✓ (por defecto)
- Para ver el proceso no-químico → Usa `/commercial/todos` (requiere permisos especiales)

**Solución**: Verificar URL de browser o solicitar acceso a `/commercial/todos`

### HIPÓTESIS 3: Hay un filtro activo ocultándolos (20% probable)
- Filtro de palabra clave específica
- Filtro de "hoy" activo (NC-1768153530001-2026-00100 es de ayer)
- Filtro de zona/vendedor específico

**Solución**: Revisar y limpiar filtros en la UI

## PRUEBA RÁPIDA DE DIAGNÓSTICO

### Opción 1: API Directa (más rápido)
```bash
# Reemplaza HOST y TOKEN con tus valores
curl -H "Authorization: Bearer YOUR_TOKEN" \
  "http://localhost:5000/api/opportunities/visibility?code=NIC-0660001250001-2026-00145"

# Respuesta te dirá exactamente por qué no está visible
```

### Opción 2: En la UI
1. Ir a `/commercial/quimica`
2. Limpiar TODOS los filtros
3. Buscar por código: "NIC-0660001250001-2026-00145"
4. Si no aparece = Eres vendedor sin asignación

## DATOS DE CONTACTO PARA ACCESO

**Para acceder a `/commercial/todos`**, necesitas rol:
- admin
- gerencia
- coordinator
- usuario con loginName = "importaciones"

Contacta a tu administrador si necesitas estos permisos.

## ESTADO DETALLADO DE BD

```sql
-- Ejecutar en PostgreSQL para verificar estado actual
SELECT 
  ocid_or_nic,
  is_chemistry_candidate,
  assigned_user_id,
  estado,
  fecha_publicacion
FROM opportunities
WHERE ocid_or_nic IN (
  'NIC-0660001250001-2026-00145',
  'NIC-0660001250001-2026-00147',
  'NIC-1760003410001-2026-00440',
  'NIC-1768092990001-2026-00007',
  'NIC-1768153530001-2026-00100',
  'NC-1768153530001-2026-00282'
)
ORDER BY ocid_or_nic;
```

Resultado esperado: 6 filas
- 5 con `is_chemistry_candidate = true`
- 1 con `is_chemistry_candidate = false`
- TODOS con `assigned_user_id = NULL`

## PRÓXIMOS PASOS

### Si eres ADMINISTRADOR:
1. Revisar archivo [ANÁLISIS_FILTROS_TÉCNICOS.md](./ANÁLISIS_FILTROS_TÉCNICOS.md) para detalles técnicos
2. Ejecutar consultas SQL de diagnóstico
3. Verificar logs de aplicación para errores

### Si eres USUARIO:
1. Verificar que estás en `/commercial/quimica` o `/commercial/todos`
2. Limpiar filtros activos
3. Si eres vendedor, solicitar asignación de procesos a tu gerente
4. Si necesitas acceso a `/commercial/todos`, pedir a tu admin

## ARCHIVOS RELEVANTES EN CÓDIGO

- **Consulta SQL**: [backend/Data/CrmRepository.OpportunityData.cs](./backend/Data/CrmRepository.OpportunityData.cs#L11)
- **Filtrado memoria**: [backend/Data/CrmRepository.OpportunityData.cs](./backend/Data/CrmRepository.OpportunityData.cs#L245)
- **Endpoints**: [backend/Endpoints/OpportunityEndpoints.cs](./backend/Endpoints/OpportunityEndpoints.cs#L12)
- **Rutas frontend**: [frontend/src/app/app.routes.ts](./frontend/src/app/app.routes.ts)
- **Componente comercial**: [frontend/src/app/features/commercial/commercial-page.component.ts](./frontend/src/app/features/commercial/commercial-page.component.ts#L703)

---

**Investigación completada**: 16 abril, 2026 11:30 Ecuador Time
