param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$configDir = Join-Path $root 'config'
$credentialsFile = Join-Path $configDir 'n8n-credentials.json'

function Read-EnvFile {
    param([string]$Path)

    $map = @{}
    if (-not (Test-Path $Path)) {
        return $map
    }

    foreach ($line in (Get-Content $Path)) {
        if ($line -match '^[A-Za-z0-9_]+=' -and -not $line.StartsWith('#')) {
            $parts = $line.Split('=', 2)
            $map[$parts[0]] = $parts[1]
        }
    }

    return $map
}

function Get-ConfigValue {
    param(
        [hashtable]$Config,
        [string]$Key,
        [string]$Default = ''
    )

    if ($Config.ContainsKey($Key) -and -not [string]::IsNullOrWhiteSpace($Config[$Key])) {
        return $Config[$Key]
    }

    return $Default
}

$config = Read-EnvFile -Path $envFile
New-Item -ItemType Directory -Force -Path $configDir | Out-Null

$postgresCredential = [ordered]@{
    id = 'QX2Kr6LtP0sGmA1b'
    name = 'Local Postgres CRM'
    type = 'postgres'
    data = [ordered]@{
        host = Get-ConfigValue -Config $config -Key 'N8N_DB_HOST' -Default 'host.docker.internal'
        database = Get-ConfigValue -Config $config -Key 'POSTGRES_DB' -Default 'sercop_crm'
        user = Get-ConfigValue -Config $config -Key 'POSTGRES_USER' -Default 'sercop_local'
        password = Get-ConfigValue -Config $config -Key 'POSTGRES_PASSWORD' -Default ''
        maxConnections = 20
        allowUnauthorizedCerts = $false
        ssl = 'disable'
        port = [int](Get-ConfigValue -Config $config -Key 'N8N_DB_PORT' -Default '5434')
    }
    isManaged = $false
    isGlobal = $false
    isResolvable = $false
    resolvableAllowFallback = $false
}

$smtpCredential = [ordered]@{
    id = 'Sm7Ny4BcT2rVhP8q'
    name = 'Local SMTP Mailpit'
    type = 'smtp'
    data = [ordered]@{
        user = Get-ConfigValue -Config $config -Key 'SMTP_USER' -Default ''
        password = Get-ConfigValue -Config $config -Key 'SMTP_PASSWORD' -Default ''
        host = Get-ConfigValue -Config $config -Key 'SMTP_HOST' -Default 'mailpit'
        port = [int](Get-ConfigValue -Config $config -Key 'SMTP_PORT' -Default '1025')
        secure = $false
        disableStartTls = $true
        hostName = 'sercop-n8n.local'
    }
    isManaged = $false
    isGlobal = $false
    isResolvable = $false
    resolvableAllowFallback = $false
}

$payload = @($postgresCredential, $smtpCredential) | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($credentialsFile, $payload, [System.Text.UTF8Encoding]::new($false))

Write-Host "Importando credenciales n8n desde $credentialsFile ..."
docker compose exec -T n8n n8n import:credentials --input=/workspace/config/n8n-credentials.json | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "La importacion de credenciales n8n fallo con codigo $LASTEXITCODE."
}

Write-Host 'Credenciales n8n provisionadas.'
