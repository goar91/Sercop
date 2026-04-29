param(
    [switch]$RestartN8n
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $root '.env'
$credentialsPath = Join-Path $root 'config\temp-credentials.json'

. (Join-Path $PSScriptRoot 'PostgresTools.ps1')

function Require-ConfigValue {
    param(
        [hashtable]$Config,
        [string]$Name
    )

    if (-not $Config.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace($Config[$Name])) {
        throw "Falta la variable requerida en .env: $Name"
    }

    return $Config[$Name]
}

$config = Read-EnvConfig -Path $envPath
$dbHost = if ($config.ContainsKey('N8N_DB_HOST') -and $config['N8N_DB_HOST']) { $config['N8N_DB_HOST'] } else { 'host.docker.internal' }
$dbPort = if ($config.ContainsKey('N8N_DB_PORT') -and $config['N8N_DB_PORT']) { [int]$config['N8N_DB_PORT'] } else { 5432 }
$dbName = Require-ConfigValue -Config $config -Name 'POSTGRES_DB'
$dbUser = Require-ConfigValue -Config $config -Name 'POSTGRES_USER'
$dbPassword = Require-ConfigValue -Config $config -Name 'POSTGRES_PASSWORD'

$credentials = @(
    [ordered]@{
        id = 'QX2Kr6LtP0sGmA1b'
        name = 'Local Postgres CRM'
        type = 'postgres'
        data = [ordered]@{
            host = $dbHost
            database = $dbName
            user = $dbUser
            password = $dbPassword
            maxConnections = 100
            allowUnauthorizedCerts = $false
            ssl = 'disable'
            port = $dbPort
        }
    },
    [ordered]@{
        id = 'Sm7Ny4BcT2rVhP8q'
        name = 'Local SMTP Mailpit'
        type = 'smtp'
        data = [ordered]@{
            user = ''
            password = ''
            host = 'mailpit'
            port = 1025
            secure = $false
            disableStartTls = $false
            hostName = 'localhost'
        }
    }
)

$credentialsJson = $credentials | ConvertTo-Json -Depth 8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($credentialsPath, $credentialsJson, $utf8NoBom)

Write-Host 'Importando credenciales locales de n8n desde .env...'
docker compose exec -T n8n n8n import:credentials --input=/workspace/config/temp-credentials.json | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "n8n import:credentials devolvio codigo $LASTEXITCODE."
}

if ($RestartN8n) {
    Write-Host 'Reiniciando n8n para aplicar credenciales...'
    docker compose restart n8n | Out-Host
}

Write-Host 'Credenciales locales de n8n sincronizadas.'
