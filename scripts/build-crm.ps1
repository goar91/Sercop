param(
    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Assert-LastExitCode {
    param([string]$Action)

    if ($LASTEXITCODE -ne 0) {
        throw "$Action fallo con codigo de salida $LASTEXITCODE."
    }
}

Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'

    if (-not $SkipFrontendBuild) {
        if (-not (Test-Path 'frontend\node_modules')) {
            Write-Host 'Instalando dependencias del frontend...'
            npm install --prefix frontend
            Assert-LastExitCode -Action 'La instalacion de dependencias del frontend'
        }

        Write-Host 'Compilando frontend Angular...'
        npm run build --prefix frontend
        Assert-LastExitCode -Action 'La compilacion del frontend'
    }

    Write-Host 'Compilando backend ASP.NET Core...'
    dotnet build backend\backend.csproj -nologo -p:UseAppHost=false
    Assert-LastExitCode -Action 'La compilacion del backend'
}
finally {
    Pop-Location
}
