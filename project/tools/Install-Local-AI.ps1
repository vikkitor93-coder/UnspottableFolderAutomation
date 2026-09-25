$ErrorActionPreference = 'Stop'
$QaDir = Join-Path $env:LOCALAPPDATA 'UnspottableExpanded\QA'
New-Item -ItemType Directory -Force -Path $QaDir | Out-Null

function Find-Ollama {
    $cmd = Get-Command ollama -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'),
        (Join-Path $env:ProgramFiles 'Ollama\ollama.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

$ollama = Find-Ollama
if (-not $ollama) {
    Write-Host 'Installing Ollama from the official ollama.com installer...' -ForegroundColor Cyan
    $install = Join-Path $env:TEMP 'ollama-install.ps1'
    Invoke-WebRequest -UseBasicParsing 'https://ollama.com/install.ps1' -OutFile $install
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $install
    if ($LASTEXITCODE -ne 0) { throw 'Ollama installer failed.' }
    Start-Sleep -Seconds 2
    $ollama = Find-Ollama
    if (-not $ollama) { throw 'Ollama installed but ollama.exe could not be found.' }
}

$ramGB = [Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$vramMB = 0
$nvidia = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($nvidia) {
    try {
        $vals = & $nvidia.Source --query-gpu=memory.total --format=csv,noheader,nounits 2>$null
        if ($vals) { $vramMB = ($vals | ForEach-Object { [int]($_.Trim()) } | Measure-Object -Maximum).Maximum }
    } catch {}
}

if ($vramMB -ge 11000 -and $ramGB -ge 24) { $model = 'qwen3-vl:8b-instruct' }
elseif (($vramMB -ge 5500) -or ($ramGB -ge 16)) { $model = 'qwen3-vl:4b-instruct' }
else { $model = 'qwen3-vl:2b-instruct' }

Write-Host ("Local AI hardware estimate: RAM={0} GB, NVIDIA VRAM={1} MB" -f $ramGB,$vramMB)
Write-Host ("Selected model: {0}" -f $model) -ForegroundColor Green
Write-Host 'The model is downloaded once and then runs locally.'

$apiReady = $false
try { Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 2 | Out-Null; $apiReady=$true } catch {}
if (-not $apiReady) {
    Start-Process -FilePath $ollama -ArgumentList 'serve' -WindowStyle Hidden | Out-Null
    for ($i=0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        try { Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 2 | Out-Null; $apiReady=$true; break } catch {}
    }
}
if (-not $apiReady) { throw 'Ollama local API did not start on 127.0.0.1:11434.' }

& $ollama pull $model
if ($LASTEXITCODE -ne 0) { throw "Failed to download $model" }

@{
    provider = 'ollama'
    endpoint = 'http://127.0.0.1:11434'
    model = $model
    installedUtc = (Get-Date).ToUniversalTime().ToString('o')
    purpose = 'Unspottable QA visual/exploratory agent; deterministic runner remains authoritative for pass/fail.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $QaDir 'ai-config.json') -Encoding UTF8

$body = @{
    model = $model
    stream = $false
    messages = @(@{ role='user'; content='Reply exactly with QA_READY' })
} | ConvertTo-Json -Depth 6
try {
    $r = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:11434/api/chat' -ContentType 'application/json' -Body $body -TimeoutSec 180
    Write-Host ("Local AI test response: {0}" -f $r.message.content) -ForegroundColor Green
} catch {
    Write-Warning ("Model downloaded, but the first inference test failed: " + $_.Exception.Message)
}
Write-Host ("AI config: {0}" -f (Join-Path $QaDir 'ai-config.json'))
