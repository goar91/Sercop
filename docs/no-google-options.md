# Modo local sin Google

## Opcion recomendada: ya implementada

El sistema queda operando con estos componentes:

- `n8n` para recolectar, filtrar, analizar y alertar.
- `PostgreSQL local` como base unica de oportunidades, analisis, usuarios y zonas.
- `CRM` Angular + C# para visualizar, asignar y hacer seguimiento.
- `Mailpit` como bandeja SMTP local para pruebas y auditoria del correo saliente.

Esta es la mejor opcion para HDM porque:

- evita OAuth y dependencias externas;
- deja todo auditable en la base local;
- permite reportes y CRM sobre los mismos datos;
- simplifica backups y soporte.

## Otras opciones si luego quieres cambiar el canal de salida

### 1. Email por SMTP

Ya esta soportado. Es la salida mas simple para avisar al responsable de procesos nuevos.

### 2. Telegram o WhatsApp

Se puede agregar desde n8n con webhook o API externa para alertas a celulares, manteniendo PostgreSQL como base principal.

### 3. Slack o Microsoft Teams

Sirve si luego quieres que la asignacion comercial se mueva a un canal interno del equipo.

### 4. Exportacion CSV o carpeta compartida

n8n puede generar archivos locales periodicos si quieres una salida adicional para gerencia o archivo.

## Fuente de verdad

En modo sin Google, la fuente de verdad es:

- tabla `opportunities` en PostgreSQL local;
- CRM local en `http://localhost:5050`;
- workflows de n8n en `http://localhost:5678`;
- bandeja de correo local en `http://localhost:8025`.
