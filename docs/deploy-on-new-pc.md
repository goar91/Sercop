# Despliegue en otro PC

Esta guia sirve para mover el sistema completo a un PC nuevo y dejarlo operativo sin depender de este servidor.

## 1. Que contiene el bundle

El bundle de migracion incluye:

- codigo fuente del backend, frontend, scripts y workflows
- `.env.current` con la configuracion actual del sistema
- `docker-compose.yml`
- scripts de arranque y detencion
- guia de despliegue
- respaldo de PostgreSQL en formato `directory dump` de PostgreSQL si el bundle se genero sin `-SkipDatabaseBackup`

Por defecto, el bundle excluye `public.execution_data` de n8n para evitar mover gigabytes de payload historico que no son necesarios para desplegar el sistema en otro PC.

El bundle no incluye:

- `.git`
- artefactos de build (`bin`, `obj`, `dist`, `.dotnet`)
- logs, `run`, `tmp`, `backups`

## 2. Requisitos del PC nuevo

Instala esto antes de restaurar el sistema:

1. Windows 10/11 de 64 bits.
2. Docker Desktop.
3. PostgreSQL 18 en la ruta estandar `C:\Program Files\PostgreSQL\18\`.
4. .NET SDK 10.
5. Node.js 22 LTS.
6. PowerShell 7 recomendado.

## 3. Copiar el bundle

1. Copia la carpeta completa del bundle al PC nuevo.
2. Muevela a la ruta final donde vivira el sistema, por ejemplo `C:\Automatización`.
3. Entra a la carpeta `package`.

## 4. Restaurar el archivo de entorno

1. Renombra `.env.current` a `.env`.
2. Revisa estos valores antes de arrancar:
   - `POSTGRES_PASSWORD`
   - `CRM_AUTH_BOOTSTRAP_PASSWORD`
   - `N8N_BASIC_AUTH_PASSWORD`
   - `CRM_EXTERNAL_ACCESS_MODE`
   - `NGROK_AUTHTOKEN` si vas a usar `ngrok`
3. Si cambias el host o el puerto local de PostgreSQL, ajusta:
   - `CRM_DB_HOST`
   - `CRM_DB_PORT`
   - `N8N_DB_HOST`
   - `N8N_DB_PORT`

## 5. Crear la base local

1. Abre PowerShell dentro de la carpeta `package`.
2. Ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\init-local-postgres.ps1 -AdminUser postgres -AdminPassword TU_CLAVE_POSTGRES
```

Esto crea o actualiza:

- la base `sercop_crm`
- el usuario de aplicacion
- el esquema y los permisos base

## 6. Restaurar el respaldo de datos

Usa esta opcion si quieres mover tambien:

- oportunidades
- usuarios
- actividades comerciales
- configuracion operativa
- tablas de n8n guardadas en PostgreSQL

Ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-local-postgres-backup.ps1 -BackupFile .\database-backup\sercop_crm-AAAAmmdd-HHmmss.dir -AdminUser postgres -AdminPassword TU_CLAVE_POSTGRES
```

Si no quieres mover los datos historicos, omite este paso y trabaja solo con el esquema limpio.

Si alguna vez necesitas copiar tambien el historial completo de payloads de n8n, genera el bundle con:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-deployment-bundle.ps1 -IncludeN8nExecutionPayloads
```

## 7. Preparar Docker y modelos

1. Abre Docker Desktop y espera a que quede operativo.
2. Desde la carpeta `package`, ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\pull-models.ps1
```

Esto descarga los modelos configurados en `.env` para Ollama.

## 8. Compilar el CRM

Ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-crm.ps1
```

## 9. Arrancar todo el sistema

Para iniciar todo:

```powershell
iniciar-automatizacion.cmd
```

Para detener todo:

```powershell
detener-automatizacion.cmd
```

## 10. Verificacion

Ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify-stack.ps1 -Live
```

Comprueba estas URLs:

- CRM: `http://localhost:5050/login`
- API CRM: `http://localhost:5050/api/health`
- Swagger: `http://localhost:5050/swagger`
- n8n: `http://localhost:5678`
- Mailpit: `http://localhost:8025`

## 11. Credenciales

Las credenciales activas salen del archivo `.env`.

Las principales son:

- `CRM_ADMIN_LOGIN`
- `CRM_AUTH_BOOTSTRAP_PASSWORD`
- `N8N_BASIC_AUTH_USER`
- `N8N_BASIC_AUTH_PASSWORD`

## 12. Acceso externo desde celular

Si quieres publicar el CRM desde el PC nuevo:

1. deja `CRM_EXTERNAL_ACCESS_MODE=cloudflare_quick` para una URL temporal
2. o configura `NGROK_AUTHTOKEN` si vas a usar `ngrok`
3. inicia el sistema con `iniciar-automatizacion.cmd`
4. revisa la URL publica en `run\crm-external-url.txt`

## 13. Cuando conviene usar restauracion completa

Usa restauracion completa si quieres conservar:

- historial comercial
- asignaciones
- usuarios y roles
- configuracion operativa
- estado actual de n8n

Usa instalacion limpia si solo quieres mover el codigo y reconstruir desde cero.

## 14. Orden recomendado

1. copiar bundle
2. restaurar `.env`
3. instalar prerrequisitos
4. crear base local
5. restaurar dump
6. descargar modelos
7. compilar CRM
8. iniciar sistema
9. verificar URLs y login
