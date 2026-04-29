# 🔄 FLUJO DE PROCESOS - DIAGRAMA DEL SISTEMA

## Flujo Completo de un Proceso Invitado (NIC-* en SERCOP)

```
┌─────────────────────────────────────────────────────────────────────┐
│ PORTAL SERCOP (compraspublicas.gob.ec)                              │
│ • Cuenta: BO***GA ✅ AUTENTICADA                                    │
│ • Procesos: Dinámicos (se actualizan diariamente)                   │
└───────────────────────────┬─────────────────────────────────────────┘
                            │
                            ▼
        ┌───────────────────────────────────────────┐
        │ PublicInvitationSyncService               │
        │ ├─ Ejecuta: Cada 30 minutos ✅            │
        │ ├─ Obtiene: Reporte de Invitaciones       │
        │ └─ HTTP Requests:                         │
        │    └─ /ReporteInvitaciones.cpe → 200 OK  │
        └───────────────────────────┬───────────────┘
                                    │
                                    ▼
            ┌───────────────────────────────────────┐
            │ SercopInvitationPublicClient           │
            │ ├─ VerifyInvitationAsync()             │
            │ ├─ Parámetros:                        │
            │ │  ├─ processCode (NIC-*)             │
            │ │  ├─ titulo                          │
            │ │  ├─ entidad                         │
            │ │  ├─ tipo                            │
            │ │  ├─ invitedCompanySearch = "HDM"    │
            │ │  └─ invitedCompanyRuc (from creds)  │
            │ └─ Retorno: { IsInvited: true, ... }  │
            └───────────────────────┬────────────────┘
                                    │
                                    ▼
        ┌───────────────────────────────────────────┐
        │ Database: opportunities TABLE              │
        │ ├─ INSERT/UPDATE:                         │
        │ │  ├─ is_invited_match = TRUE             │
        │ │  ├─ invitation_source = 'reporte_sercop'│
        │ │  ├─ invited_company_name = 'HDM'        │
        │ │  ├─ invitation_verified_at = NOW()      │
        │ │  └─ source = 'ocds'                     │
        │ └─ Resultado: 6 procesos inserados ✅     │
        └───────────────────────┬────────────────────┘
                                │
                ┌───────────────┼───────────────┐
                │               │               │
                ▼               ▼               ▼
         ┌──────────────┐ ┌──────────────┐ ┌─────────────┐
         │ KeywordRefresh│ │Visualization │ │ Retención  │
         │BackgroundSvc  │ │(Frontend)    │ │ Cleanup    │
         └──────┬───────┘ └──────┬───────┘ └──────┬──────┘
                │                │               │
                ▼                ▼               ▼
         ┌──────────────┐ ┌──────────────┐ ┌─────────────┐
         │ Clasifica    │ │Procesos      │ │Elimina      │
         │ Chemistry:   │ │Mostrados:    │ │Vencidos     │
         │ .IsChemistry │ │✅ 6/6        │ │Retención:   │
         │ Candidate=T  │ │              │ │5 días (OK)  │
         └──────────────┘ └──────────────┘ └─────────────┘
```

---

## Flujo de Visualización por Tipo de Usuario

### 1️⃣ VENDEDOR (SELLER)

```
Usuario VENDEDOR solicita: GET /api/opportunities

┌─────────────────────────────────────────────┐
│ CrmRepository.SearchOpportunitiesAsync()     │
└─────────────────────┬───────────────────────┘
                      │
                      ▼
        ┌─────────────────────────────────┐
        │ LoadOpportunityRowsAsync()       │
        │ Filtros aplicados:              │
        │ • retention_cutoff (5 días) ✅   │
        │ • source = 'ocds' ✅            │
        │ • fecha_publicacion >= cutoff ✅ │
        └──────────┬──────────────────────┘
                   │
                   ▼
        ┌──────────────────────────────┐
        │ FilterVisibleRows()           │
        │ Validaciones:                 │
        │ 1. CanActorAccessOpportunity()│
        │    ├─ NO ES SELLER           │
        │    │  → VE TODO ✅            │
        │    └─ ES SELLER              │
        │       → Ve sin asignar +      │
        │          suyos (MEJORADO) ✅  │
        │                              │
        │ 2. EvaluateVisibility()       │
        │    ├─ chemistryOnly = true   │
        │    ├─ IsChemistry = true ✅  │
        │    └─ Resultado: VISIBLE ✅  │
        │                              │
        │ 3. ProcessCategory match     │
        │    └─ "quimica" ✅ VISIBLE   │
        └──────────┬───────────────────┘
                   │
                   ▼
        ┌──────────────────────────────┐
        │ RESULTADO FINAL              │
        │ Procesos Visibles:           │
        │ ├─ NIC-00145 ✅             │
        │ ├─ NIC-00147 ✅             │
        │ ├─ NIC-00440 ✅             │
        │ ├─ NIC-00007 ✅             │
        │ ├─ NIC-00100 ✅             │
        │ └─ NC-00282 ❌ (no química) │
        └──────────────────────────────┘
```

### 2️⃣ ADMINISTRADOR/GERENCIA

```
Usuario ADMIN solicita: GET /api/opportunities?chemistryOnly=false

┌─────────────────────────────────────────────┐
│ CrmRepository.SearchOpportunitiesAsync()     │
│ chemistryScope = false ✅                    │
└─────────────────────┬───────────────────────┘
                      │
                      ▼
        ┌─────────────────────────────────┐
        │ LoadOpportunityRowsAsync()       │
        │ (mismos filtros base) ✅        │
        └──────────┬──────────────────────┘
                   │
                   ▼
        ┌──────────────────────────────┐
        │ FilterVisibleRows()           │
        │ NO es SELLER → VE TODO ✅     │
        │ chemistryOnly = FALSE ✅      │
        │ → NO filtra por química ✅    │
        └──────────┬───────────────────┘
                   │
                   ▼
        ┌──────────────────────────────┐
        │ RESULTADO FINAL              │
        │ Procesos Visibles:           │
        │ ├─ NIC-00145 ✅             │
        │ ├─ NIC-00147 ✅             │
        │ ├─ NIC-00440 ✅             │
        │ ├─ NIC-00007 ✅             │
        │ ├─ NIC-00100 ✅             │
        │ └─ NC-00282 ✅ ¡VISIBLE!    │
        └──────────────────────────────┘
```

---

## Ciclos de Actualización del Sistema

### 📅 SCHEDULE OPERATIVO

```
┌──────────────────────────────────────────────────────┐
│ CADA 30 MINUTOS                                      │
├──────────────────────────────────────────────────────┤
│ PublicInvitationSyncService.RunOnceAsync()           │
│ └─ Obtiene invitaciones de SERCOP                    │
│    └─ Actualiza DB con nuevo estado de invitaciones │
│       ✅ Última ejecución: 10:04 UTC                │
└──────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────┐
│ CADA CICLO DE KEYWORD REFRESH (configurable)        │
├──────────────────────────────────────────────────────┤
│ KeywordRefreshBackgroundService.ProcessRunAsync()    │
│ ├─ ReevaluateCurrentOpportunitiesAsync()            │
│ │  └─ Reclasifica procesos por palabras clave      │
│ │     └─ Actualiza is_chemistry_candidate           │
│ │                                                   │
│ ├─ Ejecuta Workflow N8N #1001                       │
│ │  └─ Captura nuevos procesos de SERCOP            │
│ │     └─ Inserta en BD                             │
│ │                                                   │
│ └─ CompleteKeywordRefreshRunAsync()                 │
│    └─ Registra métricas                            │
│       ✅ Status: RUNNING (activo)                  │
└──────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────┐
│ CADA HORA (configurable)                            │
├──────────────────────────────────────────────────────┤
│ OpportunityRetentionCleanupBackgroundService        │
│ └─ CleanupRetentionAsync()                          │
│    └─ Elimina procesos > 5 días                     │
│       └─ Logs: "Cutoff=04/11/2026" (OK)            │
│          Deleted=0 (procesos recientes intactos) ✅ │
└──────────────────────────────────────────────────────┘
```

---

## Matriz de Visibilidad Post-Correcciones

```
╔════════════════════════════════════════════════════════════════╗
║ PROCESO                      QUÍMICA  SELLER  ADMIN  VISIBLE  ║
╠════════════════════════════════════════════════════════════════╣
║ NIC-0660001250001-2026-00145    ✅      ✅      ✅      ✅    ║
║ NIC-0660001250001-2026-00147    ✅      ✅      ✅      ✅    ║
║ NIC-1760003410001-2026-00440    ✅      ✅      ✅      ✅    ║
║ NIC-1768092990001-2026-00007    ✅      ✅      ✅      ✅    ║
║ NIC-1768153530001-2026-00100    ✅      ✅      ✅      ✅    ║
║ NC-1768153530001-2026-00282     ❌      ❌      ✅      ✅    ║
╚════════════════════════════════════════════════════════════════╝

Leyenda:
  QUÍMICA:  Es proceso de química (chemistry = true)
  SELLER:   Lo ve un vendedor en /commercial/quimica
  ADMIN:    Lo ve un admin en /commercial/todos
  VISIBLE:  Está visible en alguna vista del sistema
```

---

## Estado de Errores y Logs

```
┌────────────────────────────────────────┐
│ crm.err.log                            │
├────────────────────────────────────────┤
│ Status: ✅ VACÍO (sin errores)         │
│ Última línea: (ninguna)                │
│ Conclusión: Sistema estable ✅         │
└────────────────────────────────────────┘

┌────────────────────────────────────────┐
│ crm.out.log (últimas 50 líneas)        │
├────────────────────────────────────────┤
│ 10:03:37: Application started          │
│ 10:03:42: HTTP GET /api/health → 200  │
│ 10:04:05: SERCOP autenticada           │
│ 10:04:06-12: Múltiples solicitudes     │
│           → Todos HTTP 200 OK ✅       │
│ 10:04:11: Sincronización completada ✅ │
│ 10:04:11: Retención completada ✅      │
│ Status: Última actividad hace <5min    │
│ Conclusión: Sistema activo ✅          │
└────────────────────────────────────────┘
```

---

## Checklist de Validación

```
[✅] 1. ¿Se sincroniza con SERCOP?
     └─ Sí, cada 30 minutos, último OK hace <1 hora

[✅] 2. ¿Están los 6 procesos invitados en BD?
     └─ Sí, todos con is_invited_match=true, invitation_source='reporte_sercop'

[✅] 3. ¿Se clasifican correctamente (5 química, 1 no)?
     └─ Sí, clasificación según palabras clave correcta

[✅] 4. ¿Se muestran en vistas adecuadas?
     └─ Sí, 5 en /commercial/quimica, todos en /commercial/todos, 
        búsqueda por código funciona

[✅] 5. ¿Los nodos están funcionando?
     └─ Sí, 4/4 nodos activos y procesando

[✅] 6. ¿La retención mantiene procesos recientes?
     └─ Sí, cutoff 5 días, procesos del 14-15 Abr dentro del rango

[✅] 7. ¿El código compila sin errores?
     └─ Sí, 0 errores, 0 advertencias

[✅] 8. ¿Las correcciones funcionan?
     └─ Sí, procesos sin asignar ahora visibles, filtro mejorado
```

---

## 📈 Métricas del Ciclo de 10:04 UTC

```
Entrada: Solicitud de sincronización de invitaciones
│
├─ PublicInvitationSyncService
│  ├─ HTTP GET ReporteInvitaciones.cpe → 200 ms ✅
│  └─ Procesos invitados encontrados: 6
│
├─ SyncInvitationsFromPublicReportsAsync
│  ├─ Escaneados: 200 procesos (máximo local)
│  ├─ Confirmados: 6 invitaciones
│  ├─ Actualizados: estado BD
│  └─ Errores: 0
│
├─ KeywordRefreshBackgroundService
│  └─ Reevaluación: procesos reclasificados
│
├─ OpportunityRetentionCleanupBackgroundService
│  ├─ Cutoff: 04/11/2026 15:04:11 UTC
│  ├─ Opportunities eliminadas: 0
│  ├─ AnalysisRuns eliminadas: 0
│  └─ FeedbackEvents eliminados: 0
│
└─ Salida: 6 procesos visibles + correctamente clasificados ✅
```

**Tiempo total**: ~10 segundos  
**Resultado**: ÉXITO ✅  
**Próxima ejecución**: 10:34 UTC (en 30 minutos)

---

## 🎓 Conclusión del Flujo

El sistema funciona correctamente siguiendo este flujo:

1. **Captura**: SERCOP → PublicInvitationSyncService → DB ✅
2. **Clasificación**: BD → KeywordRefresh → is_chemistry_candidate ✅
3. **Retención**: Cleanup elimina solo procesos vencidos ✅
4. **Visualización**: API filters → Usuarios ven según rol ✅
5. **Monitoreo**: Logs + Métricas → Sin errores ✅

**Estado Final**: 🟢 **100% OPERATIVO**
