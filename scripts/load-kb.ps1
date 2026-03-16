param(
    [string]$Collection = "code_kb",
    [string]$Folder = ".\\knowledge\\code",
    [string]$QdrantUrl = "http://localhost:6333",
    [string]$OllamaUrl = "http://localhost:11434",
    [string]$Model = "nomic-embed-text",
    [string[]]$IncludeExtensions = @(".md", ".txt", ".json", ".csv", ".ps1", ".yml", ".yaml"),
    [string[]]$ExcludeDirectories = @("node_modules", "dist", "bin", "obj", ".git", ".angular", "logs", "run", "tmp", ".docker", ".dotnet")
)

$ErrorActionPreference = "Stop"

function Split-Text {
    param([string]$Text, [int]$ChunkSize = 1200)
    $builder = [System.Text.StringBuilder]::new()
    $currentLength = 0
    $enumerator = [System.Globalization.StringInfo]::GetTextElementEnumerator($Text)

    while ($enumerator.MoveNext()) {
        $element = [string]$enumerator.Current
        if ($currentLength -ge $ChunkSize -and $builder.Length -gt 0) {
            $builder.ToString()
            [void]$builder.Clear()
            $currentLength = 0
        }

        [void]$builder.Append($element)
        $currentLength += $element.Length
    }

    if ($builder.Length -gt 0) {
        $builder.ToString()
    }
}

$root = Resolve-Path $Folder
$files = Get-ChildItem $root -Recurse -File | Where-Object {
    $segments = ($_.FullName -replace '/', '\') -split '\\'
    $_.Extension -in $IncludeExtensions -and -not ($segments | Where-Object { $_ -in $ExcludeDirectories } | Select-Object -First 1)
}

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

        $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        Invoke-RestMethod -Method Put -Uri "$QdrantUrl/collections/$Collection/points" -ContentType "application/json; charset=utf-8" -Body $payloadBytes | Out-Null
    }
}

Write-Host "Carga completada en la coleccion $Collection"


