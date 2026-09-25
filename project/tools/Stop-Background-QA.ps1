$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QA-Common.ps1')
$Game = 'D:\SteamLibrary\steamapps\common\Unspottable'
$QaDir = Join-Path $env:LOCALAPPDATA 'UnspottableExpanded\QA'
$statePath = Join-Path $QaDir 'state.json'
$metaPath = Join-Path $QaDir 'rewired-metadata.json'
$sessionPath = Join-Path $QaDir 'session.json'
$logPath = Join-Path $Game 'BepInEx\LogOutput.log'

$session = $null
if (Test-Path $sessionPath) {
    try { $session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json } catch {}
}

$p = $null
if ($session -and $session.pid) {
    $p = Get-Process -Id ([int]$session.pid) -ErrorAction SilentlyContinue
}
if ($null -eq $p) {
    $p = Get-Process -Name Unspottable -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($null -eq $p) {
    Write-Host 'No Unspottable QA process is running.'
} else {
    try { $p.CloseMainWindow() | Out-Null } catch {}
    Start-Sleep -Seconds 1
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $env:TEMP ("UE-QA-Evidence-" + $stamp)
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-UEQaSafeState $statePath (Join-Path $stage 'state.safe.json')
Copy-UEQaSafeMetadata $metaPath (Join-Path $stage 'rewired-metadata.safe.json')
Copy-UEQaSanitizedText $logPath (Join-Path $stage 'LogOutput.safe.log') -QaLinesOnly
if ($session) {
    [ordered]@{startedUtc=$session.startedUtc;mode=if($session.qaMode){$session.qaMode}else{$session.mode}} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'session.safe.json') -Encoding UTF8
}

$downloads = Join-Path $env:USERPROFILE 'Downloads'
if (-not (Test-Path $downloads)) { $downloads = $env:USERPROFILE }
$zip = Join-Path $downloads ("Unspottable-QA-Evidence-" + $stamp + '.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force
Remove-Item $sessionPath -Force -ErrorAction SilentlyContinue
Write-Host ("QA stopped. Evidence saved to: {0}" -f $zip) -ForegroundColor Green
