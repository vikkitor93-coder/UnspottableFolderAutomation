param(
    [switch]$SelfTest,
    [int]$Seconds = 8,
    [int]$Fps = 30
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QA-Common.ps1')

$Game = 'D:\SteamLibrary\steamapps\common\Unspottable'
$Exe = Join-Path $Game 'Unspottable.exe'
$QaDir = Join-Path $env:LOCALAPPDATA 'UnspottableExpanded\QA'
$StatePath = Join-Path $QaDir 'state.json'
$MetaPath = Join-Path $QaDir 'rewired-metadata.json'
$SessionPath = Join-Path $QaDir 'session.json'
New-Item -ItemType Directory -Force -Path $QaDir | Out-Null

if (-not (Test-Path $Exe)) { throw "Unspottable.exe not found at $Game" }
if (Get-Process -Name Unspottable -ErrorAction SilentlyContinue) {
    throw 'Unspottable is already running. Close it before starting a headless QA session.'
}

Remove-Item $StatePath -Force -ErrorAction SilentlyContinue

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$UnityLog = Join-Path $QaDir ("Unity-headless-" + $stamp + ".log")

$oldMode = $env:UE_QA_MODE
$oldHeadless = $env:UE_QA_HEADLESS
$oldLow = $env:UE_QA_LOW_IMPACT
$oldFps = $env:UE_QA_FPS
try {
    $env:UE_QA_MODE = '1'
    $env:UE_QA_HEADLESS = '1'
    $env:UE_QA_LOW_IMPACT = '1'
    $env:UE_QA_FPS = [Math]::Max(5, [Math]::Min(120, $Fps)).ToString()
    $args = "-batchmode -nographics -logFile `"$UnityLog`""
    $p = Start-Process -FilePath $Exe -ArgumentList $args -WorkingDirectory $Game -PassThru
}
finally {
    $env:UE_QA_MODE = $oldMode
    $env:UE_QA_HEADLESS = $oldHeadless
    $env:UE_QA_LOW_IMPACT = $oldLow
    $env:UE_QA_FPS = $oldFps
}

try {
    Start-Sleep -Milliseconds 250
    $p.PriorityClass = 'BelowNormal'
} catch {}

$deadline = (Get-Date).AddSeconds(45)
$state = $null
while ((Get-Date) -lt $deadline) {
    if ($p.HasExited) {
        throw "Headless Unspottable exited before QA telemetry became ready. See $UnityLog"
    }
    if (Test-Path $StatePath) {
        try {
            $candidate = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
            if ($candidate.pluginVersion -eq '0.9.9' -and $candidate.heartbeat -gt 0) {
                $state = $candidate
                break
            }
        } catch {}
    }
    Start-Sleep -Milliseconds 500
}
if ($null -eq $state) {
    try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}
    throw "Headless QA telemetry did not become ready within 45 seconds. See $UnityLog and BepInEx\LogOutput.log."
}

@{
    pid = $p.Id
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    qaMode = 'headless'
} | ConvertTo-Json | Set-Content -LiteralPath $SessionPath -Encoding UTF8

Write-Host ''
Write-Host 'TRUE HEADLESS QA IS RUNNING' -ForegroundColor Green
Write-Host ("  PID:               {0}" -f $p.Id)
Write-Host ("  Scene:             {0}" -f $state.scene)
Write-Host ("  Heartbeat:         {0}" -f $state.heartbeat)
Write-Host ("  QA mode:           {0}" -f $state.qaMode)
Write-Host ("  Batch mode:        {0}" -f $state.isBatchMode)
Write-Host ("  Graphics device:   {0}" -f $state.graphicsDevice)
Write-Host ("  State:             {0}" -f $StatePath)
Write-Host ("  Unity log:         {0}" -f $UnityLog)
Write-Host ''

if (-not $SelfTest) {
    Write-Host 'No game window or rendering is required in this mode.'
    Write-Host 'This is intended for logic/navigation/objective/regression automation.'
    Write-Host 'Use "Unspottable QA - Stop and Collect" when you want to end it.'
    exit 0
}

Write-Host ("Running unattended headless heartbeat test for {0} seconds..." -f $Seconds) -ForegroundColor Cyan
$h1 = [int]$state.heartbeat
Start-Sleep -Seconds ([Math]::Max(3, $Seconds))
$state2 = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
$h2 = [int]$state2.heartbeat
$pass = ($h2 -gt $h1) -and [bool]$state2.runInBackground -and [bool]$state2.headlessRequested -and [bool]$state2.isBatchMode

$stage = Join-Path $env:TEMP ("UE-QA-Headless-" + $stamp)
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-UEQaSafeState $StatePath (Join-Path $stage 'state.safe.json')
Copy-UEQaSafeMetadata $MetaPath (Join-Path $stage 'rewired-metadata.safe.json')
Copy-UEQaSanitizedText $UnityLog (Join-Path $stage 'Unity-headless.safe.log') -QaLinesOnly
$log = Join-Path $Game 'BepInEx\LogOutput.log'
Copy-UEQaSanitizedText $log (Join-Path $stage 'LogOutput.safe.log') -QaLinesOnly
@{
    pass = $pass
    mode = 'headless'
    heartbeatBefore = $h1
    heartbeatAfter = $h2
    headlessRequested = $state2.headlessRequested
    isBatchMode = $state2.isBatchMode
    graphicsDevice = $state2.graphicsDevice
    runInBackground = $state2.runInBackground
    scene = $state2.scene
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'headless-self-test-result.json') -Encoding UTF8

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Remove-Item $SessionPath -Force -ErrorAction SilentlyContinue

$downloads = Join-Path $env:USERPROFILE 'Downloads'
if (-not (Test-Path $downloads)) { $downloads = $env:USERPROFILE }
$zip = Join-Path $downloads ("Unspottable-QA-HEADLESS-" + $stamp + '.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

if ($pass) {
    Write-Host ''
    Write-Host ("HEADLESS PASS: heartbeat advanced {0} -> {1}; batch mode confirmed." -f $h1,$h2) -ForegroundColor Green
    Write-Host ("Evidence: {0}" -f $zip)
    exit 0
}

Write-Host ''
Write-Host ("HEADLESS FAILED: heartbeat {0} -> {1}; requested={2}; batch={3}" -f $h1,$h2,$state2.headlessRequested,$state2.isBatchMode) -ForegroundColor Red
Write-Host ("Upload this evidence ZIP: {0}" -f $zip)
exit 2
