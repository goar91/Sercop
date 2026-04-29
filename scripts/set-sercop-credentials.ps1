param(
    [string]$BaseUrl = 'http://127.0.0.1:5050',
    [string]$CrmUser = 'admin',
    [string]$CrmPassword = '',
    [string]$SercopRuc = '',
    [string]$SercopUserName = '',
    [string]$SercopPassword = '',
    [switch]$Clear,
    [switch]$ShowStatus,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $root '.env'

function Import-EnvFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return
    }

    Get-Content $Path | ForEach-Object {
        if ($_ -match '^[A-Za-z0-9_]+=' -and -not $_.StartsWith('#')) {
            $parts = $_.Split('=', 2)
            [Environment]::SetEnvironmentVariable($parts[0], $parts[1], 'Process')
        }
    }
}

function Convert-SecureStringToPlainText {
    param([Security.SecureString]$SecureString)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

Import-EnvFile -Path $envPath

if ([string]::IsNullOrWhiteSpace($CrmPassword)) {
    $CrmPassword = $env:CRM_AUTH_BOOTSTRAP_PASSWORD
}

if ([string]::IsNullOrWhiteSpace($CrmPassword)) {
    throw 'No se encontro la clave del usuario CRM. Usa -CrmPassword o define CRM_AUTH_BOOTSTRAP_PASSWORD.'
}

$loginBody = @{
    identifier = $CrmUser
    password = $CrmPassword
    rememberMe = $false
} | ConvertTo-Json -Compress

Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" -Body $loginBody -ContentType 'application/json' -SessionVariable crmSession | Out-Null

if ($ShowStatus) {
    $status = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/sercop/credentials" -WebSession $crmSession
    $status | ConvertTo-Json -Depth 5
    exit 0
}

if ($Test) {
    $status = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/sercop/credentials/test" -WebSession $crmSession
    $status | ConvertTo-Json -Depth 5
    exit 0
}

if ($Clear) {
    Invoke-RestMethod -Method Delete -Uri "$BaseUrl/api/sercop/credentials" -WebSession $crmSession | Out-Null
    Write-Host 'Credenciales SERCOP eliminadas del backend.'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($SercopRuc)) {
    throw 'Debes indicar -SercopRuc.'
}

if ([string]::IsNullOrWhiteSpace($SercopUserName)) {
    throw 'Debes indicar -SercopUserName.'
}

if ([string]::IsNullOrWhiteSpace($SercopPassword)) {
    $securePassword = Read-Host 'Clave SERCOP' -AsSecureString
    $SercopPassword = Convert-SecureStringToPlainText -SecureString $securePassword
}

$payload = @{
    ruc = $SercopRuc
    userName = $SercopUserName
    password = $SercopPassword
} | ConvertTo-Json -Compress

$status = Invoke-RestMethod -Method Put -Uri "$BaseUrl/api/sercop/credentials" -Body $payload -ContentType 'application/json' -WebSession $crmSession

Write-Host 'Credenciales SERCOP guardadas de forma segura en el backend.'
Write-Host "RUC enmascarado: $($status.maskedRuc)"
Write-Host "Usuario enmascarado: $($status.maskedUserName)"
Write-Host "Estado de validacion: $($status.validationStatus)"
Write-Host "Estado de sesion portal: $($status.portalSessionStatus)"
