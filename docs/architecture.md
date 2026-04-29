# Arquitectura

## Flujo operativo

1. `07_sercop_pc_public_poller.json` captura procesos PC publicos (ventana reciente) desde el portal SERCOP y hace `upsert` en PostgreSQL.
2. `02_sercop_nco_poller.json` captura el listado NCO (NIC/NC), guarda el universo publico reciente y en paralelo aplica reglas de palabras clave para el modulo Quimica.
3. `01_sercop_ocds_poller.json` consulta el API OCDS por keywords (complemento) y normaliza los procesos a la misma tabla `opportunities`.
4. El CRM muestra lo guardado en PostgreSQL con retencion por `fecha_publicacion` (por defecto 5 dias) y limpieza automatica.
5. `03_sercop_manual_analysis.json` sirve para validaciones manuales y soporte operativo.
6. `06_feedback_weekly.json` resume feedback operativo para ajustar reglas.

## Servicios

- `postgres`: estado interno, oportunidades, documentos, feedback y audit trail.
- `n8n`: workflows, credenciales y ejecuciones.
- `mailpit`: SMTP local para capturar correos de notificacion.
- `crm-ngrok`: tunel HTTPS saliente para exponer solo el CRM externamente.

## Contratos internos

- `opportunities.source`: `ocds` o `nco`.
- `opportunities.external_id`: `ocid`, `id` o `nic` externo.
- `opportunities.invited_company_name`: empresa objetivo de la invitacion detectada.
- `opportunities.is_invited_match`: confirmacion booleana del filtro de invitacion.
- `documents.source_url`: URL publica del PDF o pagina origen.

## Seguridad minima

- Mantener `N8N_BASIC_AUTH_ACTIVE=true`.
- Cambiar `N8N_BASIC_AUTH_PASSWORD` y `POSTGRES_PASSWORD`.
- Definir `CRM_ADMIN_LOGIN` y `CRM_AUTH_BOOTSTRAP_PASSWORD`.
- Mantener `CRM_REQUIRE_HTTPS=true` para accesos externos.
- Usar `N8N_ENCRYPTION_KEY` distinta por entorno.
- No exponer el CRM sin `NGROK_AUTHTOKEN`.
- Preferir `ngrok` o un dominio propio antes que abrir puertos del router.
- No subir `.env` al repositorio.




