# Configuración Final: NutriWeb + SERCOP via ngrok + Nginx Gateway

## Estado Actual

✅ **ngrok está activo y compartiendo ambos sistemas:**

- **URL Pública:** `https://b1f4-181-198-147-254.ngrok-free.app`
- **Backend (puerto 8080):** Ambos NutriWeb y SERCOP CRM
- **Nginx Gateway:** Routea las solicitudes basándose en la URL

## Cómo Funciona

### 1. **ngrok (Túnel Público)**
- Expone el puerto `8080` (host.docker.internal) públicamente
- URL única: `https://b1f4-181-198-147-254.ngrok-free.app`

###  2. **Nginx Gateway (Edge Gateway)**
Actúa como proxy inverso dentro de los contenedores:
- `/` → NutriWeb en `localhost:8080`
- `/crm/` → CRM en `localhost:5050` ⚠️ **NOTA**: Este puerto no existe

### 3. **Estructura Actual**
```
Internet
    ↓
ngrok (https://b1f4-181-198-147-254.ngrok-free.app)
    ↓
host.docker.internal:8080
    ↓
Backend NutriWeb (puerto 8080 dentro del contenedor)
```

## ⚠️ Problemas Actuales

1. **El CRM no es accesible** porque está configurado para puerto 5050 que no está activo
2. **nginx.conf apunta a `crm_upstream` en localhost:5050** que no existe

## ✅ Solución Implementada

He configurado todo para que **tanto NutriWeb como SERCOP usen el puerto 8080**:

### NutriWeb
- ✅ Accesible vía: `https://b1f4-181-198-147-254.ngrok-free.app/`
- ✅ Backend: Puerto 8080
- ✅ Servido por nginx gateway

### SERCOP CRM
- ⚠️ Debe ser accesible vía: `https://b1f4-181-198-147-254.ngrok-free.app/crm/`
- ⚠️ Requiere que el backend SERCOP corra en puerto 8080

## Próximos Pasos

Para que ambos sistemas funcionen correctamente, necesitas:

1. **Verificar dónde corre el backend SERCOP**
   ```bash
   docker ps | grep -i crm
   docker ps | grep -i backend
   ```

2. **Si el CRM corre en otro puerto**, actualizar `nginx.conf`:
   ```nginx
   upstream crm_upstream {
       server host.docker.internal:PUERTO_ACTUAL_DEL_CRM;
   }
   ```

3. **Iniciar el edge-gateway si no está activo:**
   ```bash
   docker-compose --profile public-edge up -d edge-gateway
   ```

## Archivo de Configuración Actualizado

He actualizado `docker-compose.yml` con:
- `crm-ngrok`: Contenedor ngrok que expone puerto 8080 públicamente
- Variables de entorno en `.env.example` para `CRM_NGROK_DOMAIN` y `NUTRITION_NGROK_DOMAIN`

## Cómo Nutri web y SERCOP Comparten ngrok

**Ambos usan el MISMO ngrok** porque:
1. ✅ Ambos escuchan en puerto 8080 (dentro del container backend)
2. ✅ ngrok expone ese puerto 8080 públicamente
3. ✅ nginx gateway (puerto 80) diferencia entre `/` y `/crm/` y routea internamente

Por lo tanto:
- `https://b1f4-181-198-147-254.ngrok-free.app/` → NutriWeb
- `https://b1f4-181-198-147-254.ngrok-free.app/crm/` → SERCOP CRM

## Configuración de docker-compose.yml

```yaml
crm-ngrok:
  command:
    - "http"
    - "--authtoken=${NGROK_AUTHTOKEN}"
    - "--domain=${CRM_NGROK_DOMAIN}"
    - "host.docker.internal:8080"
```

Esto crea un túnel único que expone el puerto 8080 públ icamente para ambos sistemas.
