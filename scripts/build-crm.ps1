param(
    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Resolve-DotnetExecutable {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidate = Join-Path ${env:ProgramFiles} 'dotnet\dotnet.exe'
    if (Test-Path $candidate) {
        return $candidate
    }

    throw 'No se encontro dotnet en PATH ni en C:\Program Files\dotnet\dotnet.exe.'
}

function Resolve-NpmExecutable {
    $command = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidate = Join-Path ${env:ProgramFiles} 'nodejs\npm.cmd'
    if (Test-Path $candidate) {
        return $candidate
    }

    throw 'No se encontro npm.cmd en PATH ni en C:\Program Files\nodejs\npm.cmd.'
}

Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $dotnet = Resolve-DotnetExecutable
    $npm = Resolve-NpmExecutable

    if (-not $SkipFrontendBuild) {
        Write-Host 'Compilando frontend Angular...'

        $frontendDir = Join-Path $root 'frontend'
        $packageJson = Join-Path $frontendDir 'package.json'
        if (-not (Test-Path $packageJson)) {
            throw "No se encontro $packageJson. Falta restaurar el source del frontend."
        }

        $nodeModules = Join-Path $frontendDir 'node_modules'
        $semverPackage = Join-Path $nodeModules 'semver\package.json'
        if (-not (Test-Path $nodeModules) -or -not (Test-Path $semverPackage)) {
            if (Test-Path (Join-Path $frontendDir 'package-lock.json')) {
                Write-Host 'Instalando dependencias del frontend (npm ci)...'
                & $npm ci --prefix frontend
            }
            else {
                Write-Host 'Instalando dependencias del frontend (npm install)...'
                & $npm install --prefix frontend
            }
        }

        & $npm run build --prefix frontend
    }

    Write-Host 'Compilando backend ASP.NET Core...'
    & $dotnet build backend\backend.csproj -nologo -p:UseAppHost=false
}
finally {
    Pop-Location
}
