param(
    [string]$Model = '',
    [string]$Prompt = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'

function Read-EnvFile {
    param([string]$Path)

    $map = @{}
    if (-not (Test-Path $Path)) {
        return $map
    }

    Get-Content $Path | ForEach-Object {
        if ($_ -match '^[A-Za-z0-9_]+=' -and -not $_.StartsWith('#')) {
            $parts = $_.Split('=', 2)
            $map[$parts[0]] = $parts[1]
        }
    }

    return $map
}

function Resolve-CommandPath {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw "No se encontro '$Name'. Verifica que este instalado y disponible en PATH."
}

$config = Read-EnvFile -Path $envFile

$baseUrl = if ($config.ContainsKey('OLLAMA_BASE_URL') -and -not [string]::IsNullOrWhiteSpace($config['OLLAMA_BASE_URL'])) {
    $config['OLLAMA_BASE_URL'].Trim()
}
else {
    'http://127.0.0.1:11434'
}

$resolvedModel = if (-not [string]::IsNullOrWhiteSpace($Model)) {
    $Model.Trim()
}
elseif ($config.ContainsKey('OLLAMA_CODE_MODEL') -and -not [string]::IsNullOrWhiteSpace($config['OLLAMA_CODE_MODEL'])) {
    $config['OLLAMA_CODE_MODEL'].Trim()
}
else {
    'qwen2.5-coder:3b'
}

$ollama = Resolve-CommandPath -Name 'ollama'

try {
    Invoke-RestMethod -Uri "$baseUrl/api/version" -TimeoutSec 5 | Out-Null
}
catch {
    Write-Host 'Ollama no respondio. Intentando iniciar el servidor local...'
    Start-Process $ollama -ArgumentList 'serve' -WindowStyle Hidden | Out-Null
    Start-Sleep -Seconds 3
}

$modelList = & $ollama list
if ($LASTEXITCODE -ne 0) {
    throw 'No se pudo consultar la lista de modelos de Ollama.'
}

if (-not (($modelList | Out-String) -match [regex]::Escape($resolvedModel))) {
    Write-Host "El modelo '$resolvedModel' no esta instalado. Descargando con ollama pull..."
    & $ollama pull $resolvedModel
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo descargar el modelo '$resolvedModel'."
    }
}

$env:OLLAMA_HOST = $baseUrl

Push-Location $root
try {
    Write-Host "Ollama directo: $baseUrl"
    Write-Host "Modelo: $resolvedModel"
    Write-Host ''

    if ([string]::IsNullOrWhiteSpace($Prompt)) {
        & $ollama run $resolvedModel
    }
    else {
        & $ollama run $resolvedModel $Prompt
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Ollama termino con codigo $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
