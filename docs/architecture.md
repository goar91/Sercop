# Arquitectura

## Flujo operativo

1. `01_sercop_ocds_poller.json` consulta el API OCDS por grupos de keywords y restringe la captura a procesos donde HDM aparezca invitada.
2. `02_sercop_nco_poller.json` recorre el listado NCO, obtiene detalles por NIC y solo deja pasar oportunidades con evidencia de invitacion a HDM.
3. Ambos flujos normalizan el payload, validan la invitacion a HDM, calculan `match_score` y hacen `upsert` en PostgreSQL.
4. Las oportunidades por encima del umbral pasan a analisis IA y quedan disponibles para alertas y asignacion.
5. `03_sercop_manual_analysis.json` y `04_sercop_chat.json` consumen Ollama y Qdrant para analisis puntual y consultas.
6. `05_programming_chat.json` usa el modelo de codigo para consultas tecnicas.
7. `06_feedback_weekly.json` resume feedback operativo para ajustar reglas.

## Servicios

- `postgres`: estado interno, oportunidades, documentos, feedback y audit trail.
- `qdrant`: indices `sercop_docs` y `code_kb`.
- `ollama`: modelos `qwen3:0.6b`, `qwen2.5-coder:0.5b`, `nomic-embed-text` en esta maquina. El runtime LLM queda instalado, pero los webhooks hoy responden con `RAG + reglas` por limite practico de CPU/RAM.
- `n8n`: workflows, credenciales y ejecuciones.
- `ngrok`: exposicion HTTPS opcional.
- `crm-cloudflared`: tunel saliente para exponer solo el CRM externamente.

## Contratos internos

- `opportunities.source`: `ocds` o `nco`.
- `opportunities.external_id`: `ocid`, `id` o `nic` externo.
- `opportunities.invited_company_name`: empresa objetivo de la invitacion detectada.
- `opportunities.is_invited_match`: confirmacion booleana del filtro de invitacion.
- `documents.source_url`: URL publica del PDF o pagina origen.
- `analysis_runs.analysis_payload`: salida estructurada del analisis IA.

## Seguridad minima

- Mantener `N8N_BASIC_AUTH_ACTIVE=true`.
- Cambiar `N8N_BASIC_AUTH_PASSWORD` y `POSTGRES_PASSWORD`.
- Definir `CRM_ADMIN_LOGIN` y `CRM_AUTH_BOOTSTRAP_PASSWORD`.
- Mantener `CRM_REQUIRE_HTTPS=true` para accesos externos.
- Usar `N8N_ENCRYPTION_KEY` distinta por entorno.
- No exponer `ngrok` sin `NGROK_AUTHTOKEN`.
- Preferir `Cloudflare Quick Tunnel` o un tunel con dominio propio antes que abrir puertos del router.
- No subir `.env` al repositorio.




