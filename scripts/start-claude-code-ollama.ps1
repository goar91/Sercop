param(
    [string]$Model = '',
    [string]$Prompt = '',
    [switch]$SkipPermissions,
    [switch]$Bare
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
    'qwen3.5'
}

$ollama = Resolve-CommandPath -Name 'ollama'
Resolve-CommandPath -Name 'claude' | Out-Null

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

$env:CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC = '1'
$env:OLLAMA_HOST = $baseUrl

Push-Location $root
try {
    $claudeArguments = @()
    if ($SkipPermissions) {
        $claudeArguments += '--dangerously-skip-permissions'
    }

    if (-not [string]::IsNullOrWhiteSpace($Prompt)) {
        $claudeArguments += @('-p', '--output-format', 'text', $Prompt)
        if ($Bare) {
            $claudeArguments = @('--bare') + $claudeArguments
        }
    }
    elseif ($Bare) {
        $claudeArguments += '--bare'
    }

    $arguments = @('launch', 'claude', '--model', $resolvedModel, '--yes')
    if ($claudeArguments.Count -gt 0) {
        $arguments += '--'
        $arguments += $claudeArguments
    }

    Write-Host "Claude Code usando Ollama: $baseUrl"
    Write-Host "Modelo: $resolvedModel"
    & $ollama @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Claude Code termino con codigo $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
