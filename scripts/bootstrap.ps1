param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env"
$exampleFile = Join-Path $root ".env.example"

if (-not (Test-Path $exampleFile)) {
    throw "No se encontro .env.example en $root"
}

if ((Test-Path $envFile) -and -not $Force) {
    Write-Host ".env ya existe. Usa -Force para reemplazarlo."
} else {
    Copy-Item $exampleFile $envFile -Force
    $randomKey = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 48 | ForEach-Object { [char]$_ })
    $content = Get-Content $envFile -Raw
    $content = $content -replace "replace-with-a-random-32-plus-char-string", $randomKey
    $content = $content -replace "change-me", ("Pwd" + (Get-Random -Minimum 100000 -Maximum 999999) + "!")
    Set-Content $envFile $content -NoNewline
    Write-Host ".env generado en $envFile"
}

$dirs = @(
    "backups",
    "logs",
    "tmp",
    "knowledge\code",
    "knowledge\sercop"
)

foreach ($dir in $dirs) {
    $path = Join-Path $root $dir
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
}

Write-Host "Bootstrap terminado."
Write-Host "Siguiente paso: completa .env y ejecuta docker compose up -d"

