$ErrorActionPreference = "Stop"

Write-Host "docker compose ps"
docker compose ps

Write-Host "n8n:"
try {
    (Invoke-WebRequest -UseBasicParsing -Uri http://localhost:5678 -TimeoutSec 10).StatusCode
} catch {
    $_.Exception.Message
}

Write-Host "ollama tags:"
try {
    (Invoke-WebRequest -UseBasicParsing -Uri http://localhost:11434/api/tags -TimeoutSec 10).Content
} catch {
    $_.Exception.Message
}

Write-Host "qdrant ready:"
try {
    (Invoke-WebRequest -UseBasicParsing -Uri http://localhost:6333/readyz -TimeoutSec 10).StatusCode
} catch {
    $_.Exception.Message
}
