param(
    [string]$NutritionRepoPath = 'C:\nutriweb-1',
    [string]$PublicBaseUrl = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gatewayUrlFile = Join-Path $root 'run\public-edge-url.txt'
$crmIpUrlFile = Join-Path $root 'run\crm-public-ip-url.txt'

function Test-Url {
    param(
        [string]$Uri,
        [int]$TimeoutSec = 20
    )

    $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec $TimeoutSec
    return $response
}

function Resolve-PublicBaseUrl {
    param([string]$ConfiguredUrl)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredUrl)) {
        return $ConfiguredUrl.TrimEnd('/')
    }

    try {
        $ip = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 10).ip
        if (-not [string]::IsNullOrWhiteSpace($ip)) {
            return "http://$ip"
        }
    }
    catch {
    }

    throw 'No se pudo resolver la IP publica para validar el gateway.'
}

Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'run') | Out-Null
    $PublicBaseUrl = Resolve-PublicBaseUrl -ConfiguredUrl $PublicBaseUrl

    Write-Host 'Validando CRM local...'
    $crmHealth = Test-Url -Uri 'http://127.0.0.1:5050/api/health'
    if ($crmHealth.StatusCode -ne 200) {
        throw 'El CRM no esta respondiendo localmente en http://127.0.0.1:5050.'
    }

    if (-not (Test-Path $NutritionRepoPath)) {
        throw "No existe el repositorio de Nutricion en $NutritionRepoPath."
    }

    Write-Host 'Reiniciando Nutricion detras del gateway publico...'
    Push-Location $NutritionRepoPath
    try {
        docker compose up -d
        if ($LASTEXITCODE -ne 0) {
            throw 'No se pudo reiniciar Nutricion con la nueva configuracion de puertos.'
        }
    }
    finally {
        Pop-Location
    }

    Write-Host 'Levantando gateway publico en el puerto 80...'
    docker compose --profile public-edge up -d edge-gateway
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo iniciar el gateway publico.'
    }

    Start-Sleep -Seconds 5

    $nutritionPublic = Test-Url -Uri "$PublicBaseUrl/"
    $crmPublic = Test-Url -Uri "$PublicBaseUrl/crm/api/health"

    if ($nutritionPublic.StatusCode -ne 200) {
        throw "Nutricion no quedo accesible en $PublicBaseUrl/."
    }

    if ($crmPublic.StatusCode -ne 200) {
        throw "El CRM no quedo accesible en $PublicBaseUrl/crm/."
    }

    Set-Content -Path $gatewayUrlFile -Value $PublicBaseUrl -Encoding ASCII
    Set-Content -Path $crmIpUrlFile -Value "$PublicBaseUrl/crm/" -Encoding ASCII

    Write-Host "Nutricion publicada en: $PublicBaseUrl/"
    Write-Host "CRM publicado en: $PublicBaseUrl/crm/"
}
finally {
    Pop-Location
}
