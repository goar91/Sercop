function Read-EnvConfig {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "No existe el archivo de entorno: $Path"
    }

    $config = @{}

    Get-Content $Path | ForEach-Object {
        $line = $_.Trim()
        if (-not $line -or $line.StartsWith('#') -or $line -notmatch '^[A-Za-z0-9_]+=') {
            return
        }

        $parts = $line.Split('=', 2)
        $config[$parts[0]] = $parts[1]
    }

    return $config
}

function Find-PostgresExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $programFiles = ${env:ProgramFiles}
    $versions = '18', '17', '16', '15', '14', '13'

    foreach ($version in $versions) {
        $candidate = Join-Path $programFiles "PostgreSQL\$version\bin\$Name"
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "No se encontro $Name en PATH ni en una instalacion estandar de PostgreSQL."
}
