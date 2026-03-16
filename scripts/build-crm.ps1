param(
    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'

    if (-not $SkipFrontendBuild) {
        Write-Host 'Compilando frontend Angular...'
        npm run build --prefix frontend
    }

    Write-Host 'Compilando backend ASP.NET Core...'
    dotnet build backend\backend.csproj -nologo -p:UseAppHost=false
}
finally {
    Pop-Location
}
