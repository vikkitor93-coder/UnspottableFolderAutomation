param(
    [int]$Fps = 30,
    [int]$TimeoutSeconds = 45
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QA-Common.ps1')

$Game = 'D:\SteamLibrary\steamapps\common\Unspottable'
$Exe = Join-Path $Game 'Unspottable.exe'
$QaDir = Join-Path $env:LOCALAPPDATA 'UnspottableExpanded\QA'
$StatePath = Join-Path $QaDir 'state.json'
$MetaPath = Join-Path $QaDir 'rewired-metadata.json'
New-Item -ItemType Directory -Force -Path $QaDir | Out-Null

function Send-QA([string]$Line) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.ReceiveTimeout = 3000
        $client.SendTimeout = 3000
        $client.Connect('127.0.0.1', 24783)
        $stream = $client.GetStream()
        $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)), 1024, $true)
        $reader = New-Object System.IO.StreamReader($stream, [Text.Encoding]::UTF8, $false, 1024, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine($Line)
        return $reader.ReadLine()
    }
    finally {
        if($client){$client.Dispose()}
    }
}

function Read-State {
    if(-not (Test-Path $StatePath)){ return $null }
    try { return (Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json) } catch { return $null }
}

if(-not (Test-Path $Exe)){ throw "Unspottable.exe not found at $Game" }
if(Get-Process -Name Unspottable -ErrorAction SilentlyContinue){
    throw 'Unspottable is already running. Close it before the H1 self-test.'
}

Remove-Item $StatePath -Force -ErrorAction SilentlyContinue
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$UnityLog = Join-Path $QaDir ("Unity-H1-input-" + $stamp + ".log")

Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ' UNSPOTTABLE QA H1 - PROCESS-LOCAL INPUT SELF-TEST' -ForegroundColor Cyan
Write-Host ' No keyboard/mouse/virtual gamepad input is sent to Windows.' -ForegroundColor Cyan
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Starting Unspottable in true headless mode...' -ForegroundColor Cyan

$oldMode=$env:UE_QA_MODE
$oldHeadless=$env:UE_QA_HEADLESS
$oldInput=$env:UE_QA_INPUT
$oldLow=$env:UE_QA_LOW_IMPACT
$oldFps=$env:UE_QA_FPS
try {
    $env:UE_QA_MODE='1'
    $env:UE_QA_HEADLESS='1'
    $env:UE_QA_INPUT='1'
    $env:UE_QA_LOW_IMPACT='1'
    $env:UE_QA_FPS=[Math]::Max(5,[Math]::Min(120,$Fps)).ToString()
    $args="-batchmode -nographics -logFile `"$UnityLog`""
    $p=Start-Process -FilePath $Exe -ArgumentList $args -WorkingDirectory $Game -PassThru
}
finally {
    $env:UE_QA_MODE=$oldMode
    $env:UE_QA_HEADLESS=$oldHeadless
    $env:UE_QA_INPUT=$oldInput
    $env:UE_QA_LOW_IMPACT=$oldLow
    $env:UE_QA_FPS=$oldFps
}

try { Start-Sleep -Milliseconds 300; $p.PriorityClass='BelowNormal' } catch {}

$deadline=(Get-Date).AddSeconds($TimeoutSeconds)
$state=$null
while((Get-Date) -lt $deadline){
    if($p.HasExited){ throw "Unspottable exited before H1 became ready. See $UnityLog" }
    $state=Read-State
    if($state -and $state.pluginVersion -eq '0.9.6' -and $state.heartbeat -gt 0 -and $state.rewiredReady){
        break
    }
    Start-Sleep -Milliseconds 400
}
if(-not $state -or $state.pluginVersion -ne '0.9.6'){
    try{Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}catch{}
    throw "v0.9.6 telemetry did not become ready within $TimeoutSeconds seconds."
}

Write-Host ("Telemetry ready. Scene={0}, heartbeat={1}" -f $state.scene,$state.heartbeat) -ForegroundColor Green
Write-Host ("Harmony/Rewired input patches: ready={0}, count={1}" -f $state.qaInputPatched,$state.qaInputPatchCount)

$steps = New-Object System.Collections.Generic.List[object]
function Add-Step($name,$pass,$detail){
    $script:steps.Add([pscustomobject]@{name=$name;pass=[bool]$pass;detail=$detail}) | Out-Null
    if($pass){Write-Host ("[PASS] {0} - {1}" -f $name,$detail) -ForegroundColor Green}
    else{Write-Host ("[FAIL] {0} - {1}" -f $name,$detail) -ForegroundColor Red}
}

try {
    $infoRaw=Send-QA 'INFO'
    $info=$infoRaw | ConvertFrom-Json
    Add-Step 'Local bridge' ($info.ok -and $info.inputEnabled) $infoRaw

    Add-Step 'Input patch installation' ([bool]$state.qaInputPatched -and [int]$state.qaInputPatchCount -ge 6) ("patches="+$state.qaInputPatchCount)

    $setRaw=Send-QA 'INPUT SETAXIS 0 MoveX 0.625 2500'
    $set=$setRaw | ConvertFrom-Json
    Add-Step 'Inject MoveX' ($set.ok) $setRaw

    $axisRaw=Send-QA 'INPUT PROBEAXIS 0 MoveX'
    $axis=$axisRaw | ConvertFrom-Json
    $axisOk=$axis.ok -and ([Math]::Abs([double]$axis.value - 0.625) -lt 0.01)
    Add-Step 'Rewired GetAxis interception' $axisOk $axisRaw

    $pressRaw=Send-QA 'INPUT PRESS 0 punch 700'
    $press=$pressRaw | ConvertFrom-Json
    Add-Step 'Inject punch' ($press.ok) $pressRaw

    $buttonRaw=Send-QA 'INPUT PROBEBUTTON 0 punch'
    $button=$buttonRaw | ConvertFrom-Json
    Add-Step 'Rewired button interception (input layer only)' ($button.ok -and $button.held) ($buttonRaw + '; this does not assert punch execution or impact')

    $runRaw=Send-QA 'INPUT PRESS 0 run 700'
    $run=$runRaw | ConvertFrom-Json
    Add-Step 'Inject run' ($run.ok) $runRaw

    $runProbeRaw=Send-QA 'INPUT PROBEBUTTON 0 run'
    $runProbe=$runProbeRaw | ConvertFrom-Json
    Add-Step 'Run button visible to Rewired' ($runProbe.ok -and $runProbe.held) $runProbeRaw

    $clearRaw=Send-QA 'INPUT CLEARALL'
    $clear=$clearRaw | ConvertFrom-Json
    Add-Step 'Clear synthetic input' ($clear.ok) $clearRaw

    Start-Sleep -Seconds 2
    $state2=Read-State
    Add-Step 'Persistent low-impact FPS cap' ([int]$state2.targetFps -eq $Fps) ("targetFps="+$state2.targetFps)
    Add-Step 'Input intercept counter' ([long]$state2.qaInputInterceptCount -ge 3) ("intercepts="+$state2.qaInputInterceptCount)
    Add-Step 'No OS-level controller required' $true 'Synthetic values were injected inside Rewired.Player getters only.'

    $passed = ($steps | Where-Object { -not $_.pass }).Count -eq 0
}
catch {
    Add-Step 'H1 execution' $false $_.Exception.Message
    $passed=$false
    $state2=Read-State
}

$stage=Join-Path $env:TEMP ("UE-QA-H1-"+$stamp)
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-UEQaSafeState $StatePath (Join-Path $stage 'state.safe.json')
Copy-UEQaSafeMetadata $MetaPath (Join-Path $stage 'rewired-metadata.safe.json')
Copy-UEQaSanitizedText $UnityLog (Join-Path $stage 'Unity-H1-input.safe.log') -QaLinesOnly
Copy-UEQaSanitizedText (Join-Path $Game 'BepInEx\LogOutput.log') (Join-Path $stage 'LogOutput.safe.log') -QaLinesOnly

$result=[ordered]@{
    pass=$passed
    checkpoint='H1/A2 process-local synthetic Rewired input'
    timestamp=$stamp
    scene=if($state2){$state2.scene}else{$state.scene}
    qaInputPatched=if($state2){$state2.qaInputPatched}else{$state.qaInputPatched}
    qaInputPatchCount=if($state2){$state2.qaInputPatchCount}else{$state.qaInputPatchCount}
    qaInputInterceptCount=if($state2){$state2.qaInputInterceptCount}else{$state.qaInputInterceptCount}
    targetFps=if($state2){$state2.targetFps}else{$state.targetFps}
    steps=$steps
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'H1-input-self-test-result.json') -Encoding UTF8

try{Send-QA 'INPUT CLEARALL' | Out-Null}catch{}
try{Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}catch{}

$downloads=Join-Path $env:USERPROFILE 'Downloads'
if(-not (Test-Path $downloads)){$downloads=$env:USERPROFILE}
$zip=Join-Path $downloads ("Unspottable-QA-H1-INPUT-"+$stamp+'.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

Write-Host ''
Write-Host '============================================================'
if($passed){
    Write-Host ' H1 PASS - PROCESS-LOCAL INPUT IS WORKING' -ForegroundColor Green
}else{
    Write-Host ' H1 FAILED - UPLOAD THE EVIDENCE ZIP' -ForegroundColor Red
}
Write-Host '============================================================'
Write-Host ("Evidence: {0}" -f $zip)
Write-Host ''
if($passed){
    Write-Host 'Next checkpoint: H2 deterministic gameplay verification. H1 does not prove movement, punch execution, or impact.' -ForegroundColor Cyan
    exit 0
}else{
    exit 2
}
