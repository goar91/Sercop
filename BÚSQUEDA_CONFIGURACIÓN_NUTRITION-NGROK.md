# Búsqueda: Configuración Previa de Nutrition-ngrok

**Fecha:** 16 de Abril de 2026  
**Búsqueda realizada por:** GitHub Copilot  
**Objetivo:** Localizar referencias a "nutrition-ngrok", "nutriweb-ngrok", "NUTRITION_NGROK" y configuración histórica de ngrok para nutriweb

---

## 📊 RESULTADOS DE LA BÚSQUEDA

### ❌ NO ENCONTRADAS
Las siguientes cadenas **NO EXISTEN** actualmente en el workspace:
- `nutrition-ngrok`
- `nutriweb-ngrok`
- `NUTRITION_NGROK`
- En archivos .md, .json, .yml, .ps1, etc.

### ✅ SI ENCONTRADAS - Configuración Actual de NutriWeb
Las referencias a nutriweb SÍ existen, pero **NO hay un contenedor ngrok separado para nutriweb**:

#### 1. **NutriWeb se ejecuta en puerto 8080** 
   - Ubicación: En contenedor Docker `goar8791/nutriweb:1.0`
   - Repositorio local: `C:\nutriweb-1`
   - Puerto interno: `8080`

#### 2. **NutriWeb se expone a través del GATEWAY de NGINX (Edge Gateway)**
   - Archivo de configuración: [config/edge-gateway/nginx.conf](config/edge-gateway/nginx.conf)
   - Contenedor Docker: `nutriweb-1-nginx:latest`
   - Puerto público: `80` (HTTP)
   - Upstream definido en nginx:
     ```nginx
     upstream nutrition_upstream {
         server host.docker.internal:8080;
         keepalive 32;
     }
     ```
   - Rutas públicas:
     - `/` → NutriWeb (puerto 8080)
     - `/crm/` → CRM (puerto 5050)

#### 3. **Exposición Externa del CRM con ngrok**
   - Contenedor: `crm-ngrok` (definido en [docker-compose.yml](docker-compose.yml))
   - Expone: `host.docker.internal:8080` (según docker-compose actual)
   - Variables de configuración:
     - `NGROK_AUTHTOKEN` (requerido)
     - `CRM_NGROK_DOMAIN` (dominio personalizado, opcional)
     - `CRM_EXTERNAL_TARGET_PORT` (puerto destino)

#### 4. **Script de Inicio del Gateway**
   - Archivo: [scripts/start-public-edge.ps1](scripts/start-public-edge.ps1)
   - Función: Reinicia nutriweb y levanta el gateway público
   - Determina IP pública y expone:
     - NutriWeb: `http://{IP_PUBLICA}/`
     - CRM: `http://{IP_PUBLICA}/crm/`

---

## 🔍 REFERENCIAS ENCONTRADAS EN BÚSQUEDA

### En [docker-compose.yml](docker-compose.yml) - ACTUAL
```yaml
# SOLO existe crm-ngrok para exponer el CRM
crm-ngrok:
  image: ${NGROK_IMAGE}
  restart: unless-stopped
  entrypoint:
    - /bin/sh
    - -lc
    - |
      set -eu
      if [ -n "${CRM_NGROK_DOMAIN}" ]; then
        ngrok http --authtoken "${NGROK_AUTHTOKEN}" --log stdout --log-format json --domain "${CRM_NGROK_DOMAIN}" host.docker.internal:8080
      else
        ngrok http --authtoken "${NGROK_AUTHTOKEN}" --log stdout --log-format json host.docker.internal:8080
      fi
```

### En [.env.example](.env.example) - ACTUAL
```
# ngrok CRM (NO hay variables para nutrition-ngrok)
NGROK_AUTHTOKEN=
NGROK_API_KEY=
NGROK_FORCE_STOP_EXISTING_SESSIONS=false

# CRM external access
CRM_EXTERNAL_TARGET_PORT=5050
CRM_NGROK_DOMAIN=
```

### En [config/edge-gateway/nginx.conf](config/edge-gateway/nginx.conf)
```nginx
limit_req_zone $binary_remote_addr zone=nutrition_login:10m rate=10r/m;

upstream nutrition_upstream {
    server host.docker.internal:8080;
    keepalive 32;
}

# NutriWeb se expone en / (raíz del gateway)
location / {
    limit_req zone=nutrition_login burst=5 nodelay;
    proxy_pass http://nutrition_upstream;
    # ... headers y configuración ...
}

# CRM se expone en /crm/
location /crm/ {
    limit_req zone=crm_login burst=3 nodelay;
    proxy_pass http://crm_upstream;
    # ... headers y configuración ...
}
```

### En [scripts/start-public-edge.ps1](scripts/start-public-edge.ps1)
- Línea 2: `[string]$NutritionRepoPath = 'C:\nutriweb-1'`
- Líneas 56-62: Reinicia nutriweb con compose del repositorio local
- Líneas 64-68: Levanta el edge-gateway (nginx)
- Líneas 70-78: Valida acceso a ambos servicios

### En [docs/architecture.md](docs/architecture.md)
- Línea 17: `- 'crm-ngrok': tunel HTTPS saliente para exponer solo el CRM externamente.`
- **NOTA:** Menciona solo `crm-ngrok`, no hay `nutrition-ngrok`

### En Auditorías ([AUDITORÍA_SISTEMA_EXHAUSTIVA.md](AUDITORÍA_SISTEMA_EXHAUSTIVA.md))
```
Servicios Docker en ejecución:
- d44d7e075e8e   ngrok/ngrok:latest      Up 7 days          ← Un solo ngrok (para CRM)
- 3495c43c5e05   n8nio/n8n:latest        Up About an hour
- e3653b212100   axllent/mailpit:latest  Up 7 days
- 665396b52620   goar8791/nutriweb:1.0   Up 2 weeks         ← NutriWeb directo
```

---

## 🏗️ ARQUITECTURA ACTUAL (CONFIRMADA)

```
┌─────────────────────────────────────────────────────────────┐
│                    INTERNET / CLIENTE                        │
└──────────────────┬──────────────────────────────────────────┘
                   │
        ┌──────────┴──────────┐
        │                     │
    ┌───▼────┐          ┌─────▼──────┐
    │ ngrok  │          │   NGINX    │
    │ port80 │          │ port80     │
    │(CRM)   │          │(Gateway)   │
    └───┬────┘          └─────┬──────┘
        │                     │
        │          ┌──────────┴────────┐
        │          │                   │
   ┌────▼──┐  ┌────▼────┐       ┌─────▼─────┐
   │ CRM   │  │NutriWeb │       │  CRM      │
   │ :5050 │  │  :8080  │       │  :5050    │
   └───────┘  └─────────┘       └───────────┘
   (cont1)     (cont2)          (cont3)
```

---

## ❓ POSIBLES ESCENARIOS

### Escenario 1: Nutriweb tuvo su propio ngrok (HISTÓRICO/REMOVIDO)
Si anteriormente existía un `nutrition-ngrok`, fue **removido de la configuración actual**.

**Indicadores de que fue removido:**
- ✅ `crm-ngrok` existe en docker-compose.yml
- ❌ `nutrition-ngrok` no existe en docker-compose.yml
- ✅ El upstream nginx apunta a puerto 8080 local (no a ngrok)
- ✅ Script `start-public-edge.ps1` maneja ambos servicios como uno solo gateway

### Escenario 2: Nutriweb NUNCA tuvo ngrok separado
La configuración actual fue diseñada así desde el inicio con el Gateway de NGINX como punto de entrada único.

---

## 📋 ARCHIVOS REVISADOS

✅ **Con referencias a nutrition/nutriweb:**
- [docker-compose.yml](docker-compose.yml)
- [scripts/start-public-edge.ps1](scripts/start-public-edge.ps1)
- [config/edge-gateway/nginx.conf](config/edge-gateway/nginx.conf)

✅ **Con referencias a ngrok:**
- [docker-compose.yml](docker-compose.yml)
- [.env.example](.env.example)
- [docs/deploy-on-new-pc.md](docs/deploy-on-new-pc.md)
- [docs/architecture.md](docs/architecture.md)
- [scripts/start-system.ps1](scripts/start-system.ps1)
- [scripts/stop-system.ps1](scripts/stop-system.ps1)

✅ **Backups históricos revisados:**
- [backups/pre-modernization_20260330_172317/docker-compose.yml](backups/pre-modernization_20260330_172317/docker-compose.yml)
  - También contiene solo `crm-ngrok`, sin `nutrition-ngrok`
- [backups/pre-modernization_20260330_172317/.env.example](backups/pre-modernization_20260330_172317/.env.example)
  - También sin variables de nutrition-ngrok

❌ **NO encontrados:**
- Archivos con nombres conteniendo "ngrok" en workspace
- Commits de git con "nutrition" en mensaje
- Variables de entorno para NUTRITION_NGROK
- Configuración histórica de nutrition-ngrok en backups disponibles

---

## ✅ RECOMENDACIONES PARA EL USUARIO

### Si necesita RECREAR la configuración anterior con nutrition-ngrok:

1. **Agregar contenedor a docker-compose.yml:**
   ```yaml
   nutrition-ngrok:
     image: ${NGROK_IMAGE}
     restart: unless-stopped
     entrypoint:
       - /bin/sh
       - -lc
       - |
         set -eu
         if [ -n "${NUTRITION_NGROK_DOMAIN}" ]; then
           ngrok http --authtoken "${NGROK_AUTHTOKEN}" --log stdout --log-format json --domain "${NUTRITION_NGROK_DOMAIN}" host.docker.internal:8080
         else
           ngrok http --authtoken "${NGROK_AUTHTOKEN}" --log stdout --log-format json host.docker.internal:8080
         fi
     environment:
       NGROK_AUTHTOKEN: ${NGROK_AUTHTOKEN}
       NUTRITION_NGROK_DOMAIN: ${NUTRITION_NGROK_DOMAIN}
     ports:
       - "4042:4040"
     profiles:
       - nutrition-ngrok
   ```

2. **Agregar variables a .env.example:**
   ```
   # ngrok NutriWeb (si se necesita ngrok adicional)
   NUTRITION_NGROK_DOMAIN=
   ```

3. **Usar el profile para iniciarlo:**
   ```powershell
   docker compose --profile nutrition-ngrok up -d nutrition-ngrok
   ```

### Si planea MANTENER la arquitectura actual (RECOMENDADO):
- El gateway de NGINX ya maneja ambos servicios de forma eficiente
- Un solo ngrok (crm-ngrok) es suficiente según diseño actual
- Costo de infraestructura reducido vs. ngrok duplicado

---

## 📝 CONCLUSIÓN

**La configuración de nutrition-ngrok NO EXISTE actualmente** en el workspace. La arquitectura actual utiliza:
- **Un solo contenedor ngrok** (`crm-ngrok`) para exponer el CRM
- **Un gateway NGINX** (`edge-gateway`) que maneja ambas aplicaciones en puerto 80 localmente
- **Un upstream nginx** que rutea a nutriweb puerto 8080

Si el usuario menciona que "ya eso estaba creado", la configuración por `nutrition-ngrok` fue **removida en favor del gateway unificado** o **nunca existió en los backups disponibles**.
