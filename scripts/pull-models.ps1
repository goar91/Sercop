$ErrorActionPreference = "Stop"

Write-Host "Descargando modelos definidos en .env..."
docker compose --profile bootstrap up ollama-init

