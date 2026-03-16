param(
    [string[]]$Codes = @(
        'SIE-GADGIRON-2026-01',
        'SIE-GADMCM-2025-26',
        'SIE-HOSNAG-2026'
    ),
    [string]$BaseUrl = 'http://localhost:5050',
    [string]$User = 'admin',
    [string]$Password = 'Pwd446301!'
)

$ErrorActionPreference = 'Stop'

$pair = "$User`:$Password"
$token = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($pair))
$headers = @{ Authorization = "Basic $token" }

$payload = @{
    codesText = ($Codes -join [Environment]::NewLine)
} | ConvertTo-Json

Write-Host 'Verificando codigos historicos contra el reporte publico de invitaciones...'
$result = Invoke-RestMethod -Method Post -UseBasicParsing -Headers $headers -ContentType 'application/json' -Uri "$BaseUrl/api/invitations/verify-codes" -Body $payload
$result | ConvertTo-Json -Depth 8

Write-Host ''
Write-Host 'Procesos visibles actualmente en el CRM con invitedOnly=true:'
$visible = Invoke-RestMethod -UseBasicParsing -Headers $headers -Uri "$BaseUrl/api/opportunities?invitedOnly=true"
@($visible).Count
