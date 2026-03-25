param(
    [string]$BaseUrl = 'http://localhost:5050',
    [string]$AdminUser = '',
    [string]$AdminPassword = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $root 'run'
$envFile = Join-Path $root '.env'

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

function Invoke-CrmJson {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session
    )

    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $Uri -WebSession $Session -ContentType 'application/json'
    }

    return Invoke-RestMethod -Method $Method -Uri $Uri -WebSession $Session -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 8)
}

function New-SeedUser {
    param(
        [string]$LoginName,
        [string]$FullName,
        [string]$Email,
        [string]$Role,
        [string]$Password,
        [Nullable[long]]$ZoneId,
        [string]$Phone = ''
    )

    return [pscustomobject]@{
        loginName = $LoginName
        fullName = $FullName
        email = $Email
        role = $Role
        phone = $(if ([string]::IsNullOrWhiteSpace($Phone)) { $null } else { $Phone })
        active = $true
        zoneId = $ZoneId
        password = $Password
        mustChangePassword = $true
    }
}

Push-Location $root
try {
    Import-EnvFile -Path $envFile
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null

    if ([string]::IsNullOrWhiteSpace($AdminUser)) {
        $AdminUser = $env:CRM_ADMIN_LOGIN
    }

    if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
        $AdminPassword = $env:CRM_AUTH_BOOTSTRAP_PASSWORD
    }

    if ([string]::IsNullOrWhiteSpace($AdminUser)) {
        $AdminUser = 'admin'
    }

    if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
        throw 'No se encontro la clave del administrador del CRM.'
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Invoke-CrmJson -Method 'POST' -Uri "$BaseUrl/api/auth/login" -Body @{
        identifier = $AdminUser
        password = $AdminPassword
        rememberMe = $true
    } -Session $session | Out-Null

    $zones = Invoke-CrmJson -Method 'GET' -Uri "$BaseUrl/api/zones" -Body $null -Session $session
    $zonePool = @($zones | Where-Object { $_.active })
    if ($zonePool.Count -eq 0) {
        $zonePool = @($zones)
    }

    $usersResponse = Invoke-CrmJson -Method 'GET' -Uri "$BaseUrl/api/users?page=1&pageSize=200" -Body $null -Session $session
    $existingUsersByLogin = @{}
    foreach ($user in $usersResponse.items) {
        $existingUsersByLogin[$user.loginName.ToLowerInvariant()] = $user
    }

    $seedUsers = New-Object System.Collections.Generic.List[object]
    $seedUsers.Add((New-SeedUser -LoginName 'importaciones' -FullName 'Importaciones' -Email 'importaciones@hdm.local' -Role 'coordinator' -Password 'Importaciones#2026!' -ZoneId $null))

    for ($index = 1; $index -le 10; $index++) {
        $zoneId = $null
        if ($zonePool.Count -gt 0) {
            $selectedZone = $zonePool[(($index - 1) % $zonePool.Count)]
            $zoneId = [long]($selectedZone | Select-Object -ExpandProperty id -First 1)
        }

        $seedUsers.Add((New-SeedUser `
            -LoginName ('vendedor{0:00}' -f $index) `
            -FullName ('Vendedor {0:00}' -f $index) `
            -Email ('vendedor{0:00}@hdm.local' -f $index) `
            -Role 'seller' `
            -Password ('Vendedor{0:00}#2026!' -f $index) `
            -ZoneId $zoneId))
    }

    $provisioned = New-Object System.Collections.Generic.List[object]

    foreach ($seedUser in $seedUsers) {
        $loginKey = $seedUser.loginName.ToLowerInvariant()
        if ($existingUsersByLogin.ContainsKey($loginKey)) {
            $existing = $existingUsersByLogin[$loginKey]
            $saved = Invoke-CrmJson -Method 'PUT' -Uri "$BaseUrl/api/users/$($existing.id)" -Body $seedUser -Session $session
        }
        else {
            $saved = Invoke-CrmJson -Method 'POST' -Uri "$BaseUrl/api/users" -Body $seedUser -Session $session
        }

        $provisioned.Add([pscustomobject]@{
            id = $saved.id
            loginName = $saved.loginName
            fullName = $saved.fullName
            role = $saved.role
            email = $saved.email
            zoneName = $saved.zoneName
            initialPassword = $seedUser.password
            mustChangePassword = $saved.mustChangePassword
        })
    }

    $csvPath = Join-Path $runDir 'usuarios-comerciales.csv'
    $provisioned | Sort-Object role, loginName | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

    Write-Host "Usuarios comerciales provisionados: $($provisioned.Count)"
    Write-Host "Archivo generado: $csvPath"
    $provisioned | Sort-Object role, loginName | Format-Table loginName, fullName, role, zoneName, initialPassword -AutoSize
}
finally {
    Pop-Location
}
