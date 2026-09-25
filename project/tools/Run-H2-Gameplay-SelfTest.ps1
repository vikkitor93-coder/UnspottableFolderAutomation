param([int]$Fps=30,[int]$TimeoutSeconds=180)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'QA-Common.ps1')

$Game=if($env:UE_GAME_DIR){$env:UE_GAME_DIR}else{'D:\SteamLibrary\steamapps\common\Unspottable'}
$Exe=Join-Path $Game 'Unspottable.exe'
$QaDir=Join-Path $env:LOCALAPPDATA 'UnspottableExpanded\QA'
$StatePath=Join-Path $QaDir 'state.json'
$MetaPath=Join-Path $QaDir 'rewired-metadata.json'
$ProbePath=Join-Path $QaDir 'lifecycle-probe.txt'
New-Item -ItemType Directory -Force -Path $QaDir|Out-Null

if(!(Test-Path $Exe)){throw "Unspottable.exe not found at $Game"}
if(Get-Process Unspottable -ErrorAction SilentlyContinue){throw 'Close Unspottable before H2 gameplay verification.'}
Remove-Item $StatePath,$ProbePath -Force -ErrorAction SilentlyContinue
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$UnityLog=Join-Path $QaDir ("Unity-H2-$stamp.log")
$stage=if($env:UE_FOLDER_QA_OUTPUT){$env:UE_FOLDER_QA_OUTPUT}else{Join-Path $env:TEMP ("UE-QA-H2-$stamp")}
New-Item -ItemType Directory -Force -Path $stage|Out-Null

Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ' UNSPOTTABLE QA H2 - DETERMINISTIC GAMEPLAY VERIFICATION' -ForegroundColor Cyan
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host 'Rendered diagnostic: normal visible game window, normal lifecycle, process-local Rewired input only.' -ForegroundColor Yellow

$old=@{m=$env:UE_QA_MODE;h=$env:UE_QA_HEADLESS;i=$env:UE_QA_INPUT;g=$env:UE_QA_GAMEPLAY;l=$env:UE_QA_LOW_IMPACT;f=$env:UE_QA_FPS}
$p=$null;$state=$null;$adapterExit=4;$bootstrapStatus='FAIL';$bootstrapReason='not started'
try{
    try{
        $env:UE_QA_MODE='1';$env:UE_QA_HEADLESS='0';$env:UE_QA_INPUT='1';$env:UE_QA_GAMEPLAY='1';$env:UE_QA_LOW_IMPACT='0';$env:UE_QA_FPS="$Fps"
        $p=Start-Process $Exe -ArgumentList "-logFile `"$UnityLog`"" -WorkingDirectory $Game -PassThru
    }finally{
        $env:UE_QA_MODE=$old.m;$env:UE_QA_HEADLESS=$old.h;$env:UE_QA_INPUT=$old.i;$env:UE_QA_GAMEPLAY=$old.g;$env:UE_QA_LOW_IMPACT=$old.l;$env:UE_QA_FPS=$old.f
    }
    Start-Sleep -Milliseconds 300

    $deadline=(Get-Date).AddSeconds($TimeoutSeconds);$last=-1
    while((Get-Date)-lt $deadline){
        if($p.HasExited){$bootstrapReason='game exited before gameplay-ready';break}
        if(Test-Path $StatePath){
            try{$state=Get-Content $StatePath -Raw|ConvertFrom-Json}catch{$state=$null}
        }
        if($state -and $state.pluginVersion -eq '0.9.8'){
            if([int]$state.qaGameplayStage -ne $last){
                $last=[int]$state.qaGameplayStage
                Write-Host ("  stage {0}: {1} | scene={2} players={3} bots={4}" -f $state.qaGameplayStage,$state.qaGameplayMessage,$state.scene,$state.playerCount,$state.botCount) -ForegroundColor DarkCyan
            }
            if($state.qaGameplayReady){$bootstrapStatus='PASS';$bootstrapReason=$state.qaGameplayMessage;break}
            if($state.qaGameplayFailed){$bootstrapReason=$state.qaGameplayMessage;break}
        }
        Start-Sleep -Milliseconds 400
    }
    if($bootstrapStatus -ne 'PASS' -and ($null -eq $state -or !$state.qaGameplayFailed) -and $bootstrapReason -eq 'not started'){$bootstrapReason="timeout after $TimeoutSeconds seconds"}

    [ordered]@{
        schemaVersion='ue.qa.bootstrap.v1';status=$bootstrapStatus;reason=$bootstrapReason
        pluginVersion=if($state){$state.pluginVersion}else{$null};scene=if($state){$state.scene}else{$null}
        qaGameplayStage=if($state){$state.qaGameplayStage}else{$null};playerCount=if($state){$state.playerCount}else{$null};botCount=if($state){$state.botCount}else{$null}
    }|ConvertTo-Json -Depth 6|Set-Content (Join-Path $stage 'H2-bootstrap.json') -Encoding UTF8

    if($bootstrapStatus -eq 'PASS'){
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-QAAdapter.ps1') -Test Gameplay -PlayerId 0 -TimeoutSeconds ([Math]::Min(20,$TimeoutSeconds)) -OutputDir $stage
        $adapterExit=$LASTEXITCODE
    } else {
        $adapterExit=2
    }
}
catch{
    $bootstrapStatus='FAIL';$bootstrapReason=$_.Exception.Message;$adapterExit=4
    [ordered]@{schemaVersion='ue.qa.bootstrap.v1';status='FAIL';reason=$bootstrapReason}|ConvertTo-Json|Set-Content (Join-Path $stage 'H2-bootstrap.json') -Encoding UTF8
}
finally{
    try{Send-UEQaCommand 'INPUT CLEARALL'|Out-Null}catch{}
    try{Send-UEQaCommand 'QA CLEANUP'|Out-Null}catch{}
    try{if($p -and !$p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}}catch{}

    Copy-UEQaSafeState $StatePath (Join-Path $stage 'state.safe.json')
    Copy-UEQaSafeMetadata $MetaPath (Join-Path $stage 'rewired-metadata.safe.json')
    Copy-UEQaSanitizedText $ProbePath (Join-Path $stage 'lifecycle-probe.safe.txt')
    Copy-UEQaSanitizedText $UnityLog (Join-Path $stage 'Unity-H2.safe.log') -QaLinesOnly
    Copy-UEQaSanitizedText (Join-Path $Game 'BepInEx\LogOutput.log') (Join-Path $stage 'LogOutput.safe.log') -QaLinesOnly
}

$downloads=Join-Path $HOME 'Downloads';if(!(Test-Path $downloads)){$downloads=$HOME}
if($env:UE_FOLDER_QA_OUTPUT){exit $adapterExit}
$zip=Join-Path $downloads ("Unspottable-QA-H2-VERIFICATION-$stamp.zip")
Compress-Archive (Join-Path $stage '*') $zip -Force
Remove-Item $stage -Recurse -Force
Write-Host ''
if($adapterExit -eq 0){Write-Host 'H2 PASS - gameplay evidence satisfied.' -ForegroundColor Green}
elseif($adapterExit -eq 3){Write-Host 'H2 SKIP/PARTIAL - a required capability was unavailable; no false PASS was emitted.' -ForegroundColor Yellow}
else{Write-Host 'H2 FAIL - targeted evidence captured.' -ForegroundColor Red}
Write-Host "Sanitized evidence: $zip"
exit $adapterExit
