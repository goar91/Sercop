param(
    [string]$Model,
    [int]$SampleSeconds = 12
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'

function Read-EnvValue {
    param(
        [string]$Path,
        [string]$Key,
        [string]$Fallback
    )

    if (-not (Test-Path $Path)) {
        return $Fallback
    }

    $line = Get-Content $Path | Where-Object { $_ -match "^$Key=" } | Select-Object -First 1
    if (-not $line) {
        return $Fallback
    }

    return $line.Split('=', 2)[1]
}

$selectedModel = if ([string]::IsNullOrWhiteSpace($Model)) {
    Read-EnvValue -Path $envFile -Key 'OLLAMA_GENERAL_MODEL' -Fallback 'qwen2.5:14b'
} else {
    $Model
}

$payload = @{
    model = $selectedModel
    prompt = 'Explica con detalle una arquitectura local de asistente personal con RAG, OCR, integracion con VS Code, automatizacion y reportes. Responde de forma tecnica para forzar inferencia real.'
    stream = $false
    options = @{
        temperature = 0.1
        num_predict = 700
    }
} | ConvertTo-Json -Depth 5

Write-Host "Probando modelo $selectedModel durante $SampleSeconds segundos..."

$job = Start-Job -ScriptBlock {
    param($RequestBody)
    Invoke-RestMethod -Method Post -Uri 'http://localhost:11434/api/generate' -ContentType 'application/json' -Body $RequestBody
} -ArgumentList $payload

Start-Sleep -Seconds 3

$samples = New-Object System.Collections.Generic.List[object]
for ($index = 0; $index -lt $SampleSeconds; $index++) {
    $rows = & nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits
    foreach ($row in $rows) {
        $parts = $row.Split(',').ForEach({ $_.Trim() })
        $samples.Add([pscustomobject]@{
            Sample = $index
            GpuIndex = [int]$parts[0]
            GpuName = $parts[1]
            Utilization = [int]$parts[2]
            MemoryUsedMiB = [int]$parts[3]
            MemoryTotalMiB = [int]$parts[4]
        })
    }
    Start-Sleep -Seconds 1
}

$result = Receive-Job $job -Wait
Remove-Job $job

$summary = $samples |
    Group-Object GpuIndex |
    ForEach-Object {
        $peakUtil = ($_.Group | Measure-Object Utilization -Maximum).Maximum
        $peakMemory = ($_.Group | Measure-Object MemoryUsedMiB -Maximum).Maximum
        [pscustomobject]@{
            GpuIndex = $_.Name
            GpuName = $_.Group[0].GpuName
            PeakUtilization = $peakUtil
            PeakMemoryMiB = $peakMemory
            UsedInTest = ($peakUtil -gt 0 -or $peakMemory -gt 0)
        }
    } |
    Sort-Object {[int]$_.GpuIndex}

$bothGpusUsed = ($summary | Where-Object { $_.UsedInTest }).Count -ge 2

Write-Host ''
Write-Host 'Resumen GPU:'
$summary | Format-Table -AutoSize | Out-String | Write-Host

Write-Host "Modelo devuelto: $($result.model)"
Write-Host "Respuesta generada: $($result.done)"
Write-Host "Longitud respuesta: $($result.response.Length)"

if (-not $bothGpusUsed) {
    throw 'La prueba no detecto uso simultaneo de ambas GPU.'
}
