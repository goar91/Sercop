# Automatizacion SERCOP + CRM HDM

Solucion local para monitorear procesos del SERCOP, confirmar invitaciones a HDM con evidencia guardada en PostgreSQL, analizarlos con n8n y operarlos desde un CRM propio en Angular + C#.

## Componentes

- `n8n` en Docker para orquestacion y webhooks.
- `PostgreSQL local` en Windows como base principal (`sercop_crm`).
- `Qdrant` en Docker para vectores.
- `Ollama` en Docker para IA local.
- `CRM` propio:
  - frontend Angular en `frontend/`
  - backend ASP.NET Core en `backend/`

## Estado actual

- n8n usa la base local PostgreSQL.
- El `postgres` de Docker quedo como perfil legacy y ya no es necesario para operar.
- Los pollers cargan procesos reales de SERCOP y nunca marcan invitacion HDM por heuristicas no verificadas.
- La invitacion HDM se confirma solo desde la base local, ya sea por validacion manual o por importacion de codigos de proceso con evidencia.
- El CRM muestra procesos, asignaciones, zonas, usuarios y workflows de n8n.
- El modo operativo recomendado ya no depende de Google.

## Estructura

- `backend/`: API C# y servidor del frontend compilado.
- `frontend/`: interfaz Angular del CRM.
- `database/init/`: esquema, CRM y permisos.
- `workflows/`: workflows importables de n8n.
- `scripts/`: bootstrap, validacion, build e inicio del CRM.
- `docs/`: arquitectura, CRM y operacion local.

## Preparacion

1. Copia `.env.example` a `.env` si todavia no existe.
2. Crea o actualiza la base local:
   - `powershell -ExecutionPolicy Bypass -File scripts\init-local-postgres.ps1`
3. Usa el lanzador de arranque:
   - `iniciar-automatizacion.cmd`
4. Si necesitas detener todo:
   - `detener-automatizacion.cmd`

## URLs

- CRM: `http://localhost:5050`
- API CRM: `http://localhost:5050/api`
- n8n: `http://localhost:5678`
- Mailpit: `http://localhost:8025`

## Acceso externo al CRM

- El modo actual intenta primero `IP publica` del CRM, luego `ngrok` y, si `ngrok` no puede abrir sesion con esa cuenta, usa un tunel saliente de respaldo.
- `n8n` y `Mailpit` no se exponen con este flujo del CRM.
- El arranque escribe la URL publica del CRM en `run\crm-external-url.txt` y tambien la muestra en consola.
- Si no quieres abrir el tunel en una sesion concreta:
  - `powershell -ExecutionPolicy Bypass -File scripts\start-system.ps1 -SkipCrmTunnel`

## Comandos utiles

- Verificacion general:
  - `powershell -ExecutionPolicy Bypass -File scripts\verify-stack.ps1 -Live`
- Inicializar la base vectorial de IA:
  - `powershell -ExecutionPolicy Bypass -File scripts\init-qdrant.ps1`
  - `powershell -ExecutionPolicy Bypass -File scripts\load-kb.ps1 -Collection sercop_docs -Folder .\knowledge\sercop`
  - `powershell -ExecutionPolicy Bypass -File scripts\load-kb.ps1 -Collection code_kb -Folder .\knowledge\code`
  - `powershell -ExecutionPolicy Bypass -File scripts\load-repo-kb.ps1`
- Descargar modelos configurados:
  - `powershell -ExecutionPolicy Bypass -File scripts\pull-models.ps1`
- Build CRM:
  - `powershell -ExecutionPolicy Bypass -File scripts\build-crm.ps1`
- Iniciar stack completo:
  - `powershell -ExecutionPolicy Bypass -File scripts\start-system.ps1`
- Detener stack completo:
  - `powershell -ExecutionPolicy Bypass -File scripts\stop-system.ps1`
- Iniciar solo CRM:
  - `powershell -ExecutionPolicy Bypass -File scripts\start-system.ps1 -SkipDocker`
- Detener solo CRM:
  - `powershell -ExecutionPolicy Bypass -File scripts\stop-system.ps1 -SkipDocker`

## Documentacion puntual

- CRM y visor de nodos: `docs\crm.md`
- Importacion de workflows: `docs\workflow-import.md`
- Operacion sin Google: `docs\no-google-options.md`
- Arquitectura general: `docs\architecture.md`

## IA y VS Code

- El asistente del CRM ya consulta `sercop_docs`, `code_kb` y `repo_code`.
- Para una version local mas fuerte, el `.env` ahora usa `qwen2.5:14b` y `qwen2.5-coder:14b` con reparto entre GPU cuando la carga lo exige.
- Si quieres una IA remota mas potente, configura `AI_PROVIDER=openai`, `OPENAI_API_KEY`, `OPENAI_GENERAL_MODEL=gpt-5.2` y `OPENAI_CODE_MODEL=gpt-5.2-codex`.
- La extension local de VS Code vive en `tools\vscode-sercop-assistant\` y consume el endpoint `/api/assistant/ask`.
