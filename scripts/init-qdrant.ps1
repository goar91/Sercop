param(
    [string]$QdrantUrl = "http://localhost:6333",
    [string]$OllamaUrl = "http://localhost:11434"
)

$ErrorActionPreference = "Stop"

$embedBody = @{
    model = "nomic-embed-text"
    input = "dimension probe"
} | ConvertTo-Json

$embedResponse = Invoke-RestMethod -Method Post -Uri "$OllamaUrl/api/embed" -ContentType "application/json" -Body $embedBody
$vectorSize = @($embedResponse.embeddings[0]).Count

if ($vectorSize -lt 1) {
    throw "No se pudo determinar la dimension del embedding."
}

$collections = @("sercop_docs", "code_kb", "repo_code")
foreach ($name in $collections) {
    try {
        Invoke-RestMethod -Method Get -Uri "$QdrantUrl/collections/$name" -TimeoutSec 10 | Out-Null
        Write-Host "Coleccion ya existente: $name"
        continue
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) {
            throw
        }
    }

    $body = @{
        vectors = @{
            size = $vectorSize
            distance = "Cosine"
        }
        optimizers_config = @{
            default_segment_number = 2
        }
    } | ConvertTo-Json -Depth 5

    Invoke-RestMethod -Method Put -Uri "$QdrantUrl/collections/$name" -ContentType "application/json" -Body $body | Out-Null
    Write-Host "Coleccion creada: $name (size=$vectorSize)"
}

