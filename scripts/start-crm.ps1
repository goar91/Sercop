param(
    [switch]$Build,
    [int]$Port = 5050
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'

function Assert-LastExitCode {
    param([string]$Action)

    if ($LASTEXITCODE -ne 0) {
        throw "$Action fallo con codigo de salida $LASTEXITCODE."
    }
}

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

Push-Location $root
try {
    Import-EnvFile -Path $envFile

    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:ASPNETCORE_URLS = "http://0.0.0.0:$Port"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'

    if ($Build -or -not (Test-Path 'frontend\dist\frontend\browser\index.html')) {
        if (-not (Test-Path 'frontend\node_modules')) {
            Write-Host 'Instalando dependencias del frontend...'
            npm install --prefix frontend
            Assert-LastExitCode -Action 'La instalacion de dependencias del frontend'
        }

        Write-Host 'Compilando frontend antes de iniciar el CRM...'
        npm run build --prefix frontend
        Assert-LastExitCode -Action 'La compilacion del frontend'
    }

    if ($Build -or -not (Test-Path 'backend\bin\Debug\net10.0\backend.dll')) {
        Write-Host 'Compilando backend antes de iniciar el CRM...'
        dotnet build backend\backend.csproj -nologo -p:UseAppHost=false
        Assert-LastExitCode -Action 'La compilacion del backend'
    }

    Write-Host "Iniciando CRM en http://0.0.0.0:$Port ..."
    dotnet backend\bin\Debug\net10.0\backend.dll
    Assert-LastExitCode -Action 'El inicio del CRM'
}
finally {
    Pop-Location
}
