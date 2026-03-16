# CRM HDM SERCOP

## URLs

- CRM: `http://localhost:5050`
- API del CRM: `http://localhost:5050/api`
- n8n: `http://localhost:5678`
- Mailpit: `http://localhost:8025`
- Ollama: `http://localhost:11434`
- Qdrant: `http://localhost:6333`

## Arranque

1. Verifica que la base local exista:
   - `powershell -ExecutionPolicy Bypass -File scripts\init-local-postgres.ps1`
2. Compila frontend y backend:
   - `powershell -ExecutionPolicy Bypass -File scripts\build-crm.ps1`
3. Inicia el CRM:
   - `powershell -ExecutionPolicy Bypass -File scripts\start-crm.ps1`
4. Abre `http://localhost:5050`.

## Que ves en el CRM

- Dashboard con totales, invitados HDM, asignados y workflows.
- Lista de procesos con filtros por estado, zona, vendedor y flag de invitacion confirmada.
- Ficha detallada del proceso con notas CRM, analisis IA, documentos e historial.
- Panel para confirmar invitacion HDM y modulo para importar codigos de procesos invitados desde el propio CRM.
- Configuracion de zonas y usuarios comerciales.
- Visor de workflows n8n con los nodos ya posicionados segun el JSON real de `workflow_entity`.

## Como ver los nodos de n8n

### Desde el CRM

1. Abre `http://localhost:5050`.
2. Baja a la seccion `Visor de nodos n8n`.
3. Selecciona uno de los workflows de la columna izquierda.
4. El panel derecho dibuja los nodos usando sus posiciones reales guardadas en la base de n8n.

### Desde n8n

1. Abre `http://localhost:5678`.
2. Ingresa con `N8N_BASIC_AUTH_USER` y `N8N_BASIC_AUTH_PASSWORD` definidos en `.env`.
3. En la lista de workflows abre cualquiera de estos:
   - `01 SERCOP OCDS Poller`
   - `02 SERCOP NCO Poller`
   - `03 SERCOP Manual Analysis`
   - `04 SERCOP Chat`
   - `05 Programming Chat`
   - `06 Feedback Weekly`
4. n8n muestra el canvas completo y puedes editar cada nodo desde su panel lateral.

## Datos locales

- Base local: `sercop_crm`
- Usuario aplicacion: `sercop_local`
- El contenedor `postgres` de Docker ya no es necesario para operar el sistema.
- n8n esta apuntando a `host.docker.internal:5432` y sigue operativo aun con el `postgres` de Docker detenido.
- El filtro `Solo procesos invitados a HDM` usa `opportunities.is_invited_match` y su evidencia asociada en PostgreSQL; no usa texto heuristico de SERCOP.
