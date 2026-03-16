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

$collections = @("sercop_docs", "code_kb")
foreach ($name in $collections) {
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
    Write-Host "Coleccion creada/actualizada: $name (size=$vectorSize)"
}

