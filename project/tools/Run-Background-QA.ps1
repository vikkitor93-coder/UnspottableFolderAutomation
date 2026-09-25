param(
    [switch]$SelfTest,
    [int]$Fps = 20
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QA-Common.ps1')

$Game = 'D:\SteamLibrary\steamapps\common\Unspottable'
$Exe = Join-Path $Game 'Unspottable.exe'
$QaDir = Join-Path $env:LOCALAPPDATA 'UnspottableExpanded\QA'
$StatePath = Join-Path $QaDir 'state.json'
$MetaPath = Join-Path $QaDir 'rewired-metadata.json'
New-Item -ItemType Directory -Force -Path $QaDir | Out-Null

if (-not (Test-Path $Exe)) { throw "Unspottable.exe not found at $Game" }
if (Get-Process -Name Unspottable -ErrorAction SilentlyContinue) { throw 'Unspottable is already running. Close it before starting a QA session.' }

Remove-Item $StatePath -Force -ErrorAction SilentlyContinue

$oldMode = $env:UE_QA_MODE
$oldLow = $env:UE_QA_LOW_IMPACT
$oldFps = $env:UE_QA_FPS
try {
    $env:UE_QA_MODE = '1'
    $env:UE_QA_LOW_IMPACT = '1'
    $env:UE_QA_FPS = [Math]::Max(5, [Math]::Min(60, $Fps)).ToString()
    $p = Start-Process -FilePath $Exe -WorkingDirectory $Game -WindowStyle Minimized -PassThru
}
finally {
    $env:UE_QA_MODE = $oldMode
    $env:UE_QA_LOW_IMPACT = $oldLow
    $env:UE_QA_FPS = $oldFps
}

try {
    Start-Sleep -Milliseconds 300
    $p.PriorityClass = 'BelowNormal'
} catch {}

try {
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UEQaWin32 {
    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
'@ -ErrorAction SilentlyContinue
    for ($i=0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $p.Refresh()
        if ($p.HasExited) { throw 'Unspottable exited before QA telemetry became ready.' }
        if ($p.MainWindowHandle -ne 0) {
            [UEQaWin32]::ShowWindowAsync($p.MainWindowHandle, 6) | Out-Null
            break
        }
    }
} catch {}

$deadline = (Get-Date).AddSeconds(35)
$state = $null
while ((Get-Date) -lt $deadline) {
    if ($p.HasExited) { throw 'Unspottable exited before QA telemetry became ready.' }
    if (Test-Path $StatePath) {
        try {
            $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
            if ($state.pluginVersion -eq '0.9.9' -and $state.heartbeat -gt 0) { break }
        } catch {}
    }
    Start-Sleep -Milliseconds 500
}
if ($null -eq $state) { throw "QA telemetry did not become ready within 35 seconds. Check BepInEx\LogOutput.log." }

@{
    pid = $p.Id
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    mode = 'background-rendered'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $QaDir 'session.json') -Encoding UTF8

Write-Host ''
Write-Host 'BACKGROUND QA IS RUNNING' -ForegroundColor Green
Write-Host ("  PID:       {0}" -f $p.Id)
Write-Host ("  Scene:     {0}" -f $state.scene)
Write-Host ("  Heartbeat: {0}" -f $state.heartbeat)
Write-Host ("  Focused:   {0}" -f $state.focused)
Write-Host ("  FPS cap:   {0}" -f $state.targetFps)
Write-Host ("  State:     {0}" -f $StatePath)
Write-Host ''

if (-not $SelfTest) {
    Write-Host 'It is safe to leave this minimized while using another application.'
    Write-Host 'Use the "Stop Unspottable QA" shortcut when you want to end the session and collect evidence.'
    exit 0
}

Write-Host 'Running A0/A1 unattended background heartbeat test...' -ForegroundColor Cyan
$h1 = [int]$state.heartbeat
Start-Sleep -Seconds 7
$state2 = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
$h2 = [int]$state2.heartbeat
$pass = ($h2 -gt $h1) -and [bool]$state2.runInBackground

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $env:TEMP ("UE-QA-SelfTest-" + $stamp)
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-UEQaSafeState $StatePath (Join-Path $stage 'state.safe.json')
Copy-UEQaSafeMetadata $MetaPath (Join-Path $stage 'rewired-metadata.safe.json')
$log = Join-Path $Game 'BepInEx\LogOutput.log'
Copy-UEQaSanitizedText $log (Join-Path $stage 'LogOutput.safe.log') -QaLinesOnly
@{
    pass = $pass
    heartbeatBefore = $h1
    heartbeatAfter = $h2
    focused = $state2.focused
    runInBackground = $state2.runInBackground
    targetFps = $state2.targetFps
    scene = $state2.scene
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'self-test-result.json') -Encoding UTF8

try { $p.CloseMainWindow() | Out-Null } catch {}
Start-Sleep -Seconds 2
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $QaDir 'session.json') -Force -ErrorAction SilentlyContinue

$downloads = Join-Path $env:USERPROFILE 'Downloads'
if (-not (Test-Path $downloads)) { $downloads = $env:USERPROFILE }
$zip = Join-Path $downloads ("Unspottable-QA-A0-A1-" + $stamp + '.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

if ($pass) {
    Write-Host ''
    Write-Host ("A0/A1 PASS: heartbeat advanced {0} -> {1} while background mode was active." -f $h1,$h2) -ForegroundColor Green
    Write-Host ("Evidence: {0}" -f $zip)
    exit 0
}

Write-Host ''
Write-Host ("A0/A1 FAILED: heartbeat {0} -> {1}, runInBackground={2}." -f $h1,$h2,$state2.runInBackground) -ForegroundColor Red
Write-Host ("Upload this evidence ZIP: {0}" -f $zip)
exit 2
