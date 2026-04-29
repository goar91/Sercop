# Configuración Actual: NutriWeb (IP Pública) + SERCOP CRM (ngrok)

## URLs de Acceso

### 1. NutriWeb - Directamente por IP Pública ✅
```
http://IP_PUBLICA:8080
```
**Características:**
- Acceso directo sin intermediarios
- Rápido, sin latencia de ngrok
- Requiere IP pública del servidor

### 2. SERCOP CRM - Por ngrok ✅
```
https://b338-181-198-147-254.ngrok-free.app
```
**Características:**
- Acceso público automatizado
- Acceso remoto desde cualquier lugar
- Controlado por token de autorización

## Arquitectura

```
┌──────────────────────────────────────┐
│         INTERNET / USUARIOS           │
├──────────────────────────────────────┤

NutriWeb              SERCOP CRM
     │                    │
     │ IP:8080            │ ngrok tunnel
     │                    │(b338-181-198...)
     ▼                    ▼
     
[Docker container]   [localhost:5050]
Port 8080            .NET Backend
```

## Configuración Docker Compose

```yaml
crm-ngrok:
  image: ngrok/ngrok:latest
  command:
    - "http"
    - "--authtoken=${NGROK_AUTHTOKEN}"
    - "--domain=${CRM_NGROK_DOMAIN}"
    - "host.docker.internal:5050"  # Puerto del CRM
  ports:
    - "4041:4040"
```

## Archivos Relacionados

- `/run/nutriweb-external-url.txt`: URL de NutriWeb (IP:8080)
- `/run/crm-external-url.txt`: URL de ngrok para SERCOP
- `/docker-compose.yml`: Configuración de contenedores
- `/.env.example`: Variables de entorno (CRM_NGROK_DOMAIN, NGROK_AUTHTOKEN)

## Ventajas de esta Configuración

✅ **NutriWeb en IP pública:**
- Bajo latency
- Control total del networking
- Ideal para intranet

✅ **SERCOP CRM por ngrok:**
- Acceso remoto automático
- Sin necesidad de configurar DNS
- Túnel seguro con autenticación

## Próximos Pasos

1. **Obtener la IP pública del servidor:**
   ```powershell
   (Invoke-WebRequest -Uri "https://api.ipify.org").Content
   ```

2. **Configurar firewall para puerto 8080**

3. **Asegurar que el CRM corre en localhost:5050:**
   ```bash
   netstat -ano | findstr :5050
   ```

4. **(Opcional) Personalizar dominio de ngrok:**
   - Editar `.env`: `CRM_NGROK_DOMAIN=mi-dominio.ngrok.io`
   - Renovar contenedor: `docker-compose --profile crm-ngrok down && docker-compose --profile crm-ngrok up -d`
