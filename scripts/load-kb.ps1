param(
    [string]$Collection = "code_kb",
    [string]$Folder = ".\\knowledge\\code",
    [string]$QdrantUrl = "http://localhost:6333",
    [string]$OllamaUrl = "http://localhost:11434",
    [string]$Model = "nomic-embed-text"
)

$ErrorActionPreference = "Stop"

function Split-Text {
    param([string]$Text, [int]$ChunkSize = 1200)
    for ($i = 0; $i -lt $Text.Length; $i += $ChunkSize) {
        $length = [Math]::Min($ChunkSize, $Text.Length - $i)
        $Text.Substring($i, $length)
    }
}

$root = Resolve-Path $Folder
$files = Get-ChildItem $root -Recurse -File | Where-Object { $_.Extension -in ".md", ".txt", ".json", ".csv", ".ps1", ".yml", ".yaml" }

if (-not $files) {
    throw "No se encontraron archivos para cargar en $root"
}

foreach ($file in $files) {
    $content = Get-Content $file.FullName -Raw
    foreach ($chunk in Split-Text -Text $content) {
        $embedBody = @{
            model = $Model
            input = $chunk
        } | ConvertTo-Json

        $embed = Invoke-RestMethod -Method Post -Uri "$OllamaUrl/api/embed" -ContentType "application/json" -Body $embedBody
        $vector = $embed.embeddings[0]

        $payload = @{
            points = @(
                @{
                    id = [Math]::Abs([Guid]::NewGuid().GetHashCode())
                    vector = $vector
                    payload = @{
                        text = $chunk
                        source_url = ($file.FullName -replace '\\','/')
                        reference = $file.Name
                    }
                }
            )
        } | ConvertTo-Json -Depth 10 -Compress

        Invoke-RestMethod -Method Put -Uri "$QdrantUrl/collections/$Collection/points" -ContentType "application/json" -Body $payload | Out-Null
    }
}

Write-Host "Carga completada en la coleccion $Collection"


