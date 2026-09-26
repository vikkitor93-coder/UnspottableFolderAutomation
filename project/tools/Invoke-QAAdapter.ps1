param(
    [ValidateSet('Gameplay')][string]$Test='Gameplay',
    [int]$PlayerId=0,
    [int]$SecondPlayerId=1,
    [int]$TimeoutSeconds=18,
    [string]$OutputDir=(Join-Path $PWD 'qa-results'),
    [double]$MovementThreshold=0.10,
    [double]$IsolationThreshold=0.05,
    [double]$PunchTargetDistance=0.85
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'QA-Common.ps1')
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$resultPath=Join-Path $OutputDir ("qa-adapter-$($Test.ToLowerInvariant())-$stamp.json")
$started=[DateTime]::UtcNow
$assertions=New-Object System.Collections.Generic.List[object]

function Add-Assertion([string]$Id,[string]$Layer,[string]$Status,[string]$Reason,$Evidence=$null,[bool]$Critical=$true){
    $script:assertions.Add([ordered]@{id=$Id;layer=$Layer;status=$Status;critical=$Critical;reason=$Reason;evidence=$Evidence})|Out-Null
    $fg=if($Status -eq 'PASS'){'Green'}elseif($Status -eq 'FAIL'){'Red'}else{'Yellow'}
    Write-Host ("[{0}] {1} - {2}" -f $Status,$Id,$Reason) -ForegroundColor $fg
}
function To-Obj([string]$raw){ if([string]::IsNullOrWhiteSpace($raw)){return $null}; return ($raw|ConvertFrom-Json) }
function Dist($a,$b){$dx=[double]$b.position.x-[double]$a.position.x;$dz=[double]$b.position.z-[double]$a.position.z;return [Math]::Sqrt($dx*$dx+$dz*$dz)}
function Find-Actor($actors,[int]$id){$matches=@($actors.actors|Where-Object{[int]$_.rewiredPlayerId -eq $id});if($matches.Count -eq 1){return $matches[0]};return $null}
function Snapshot([int]$id){ return To-Obj (Send-UEQaCommand ("QA SNAPSHOT {0}" -f $id)) }

$status='FAIL';$exitCode=4;$info=$null;$caps=$null;$actors=$null;$cleanup=[ordered]@{inputCleared=$false;worldRestored=$false}
try {
    $info=To-Obj (Send-UEQaCommand 'INFO')
    if(!$info.ok -or !$info.qaVerification){throw 'QA verification extension is unavailable. Build/deploy v0.9.10 first.'}
    $caps=To-Obj (Send-UEQaCommand 'QA CAPABILITIES')
    $actors=To-Obj (Send-UEQaCommand 'QA ACTORS')
    if(!$caps.ok -or !$actors.ok){throw 'QA capability/actor query failed.'}

    $p1=Find-Actor $actors $PlayerId
    if($null -eq $p1){
        Add-Assertion 'player.p1.ownership' 'actor ownership' 'SKIP' ("No deterministically mapped gameplay actor for Rewired player {0}." -f $PlayerId) $actors $true
    } else {
        Add-Assertion 'player.p1.ownership' 'actor ownership' 'PASS' $p1.resolution ([ordered]@{instanceId=$p1.instanceId;rewiredPlayerId=$p1.rewiredPlayerId}) $true
    }

    $p2=Find-Actor $actors $SecondPlayerId
    if($null -eq $p2){
        Add-Assertion 'player.p2.ownership' 'actor ownership' 'SKIP' 'A second independently mapped gameplay actor is not available in this bootstrap.' $null $false
    } else {
        Add-Assertion 'player.p2.ownership' 'actor ownership' 'PASS' $p2.resolution ([ordered]@{instanceId=$p2.instanceId;rewiredPlayerId=$p2.rewiredPlayerId}) $false
    }

    if($null -ne $p1){
        To-Obj (Send-UEQaCommand 'INPUT CLEARALL')|Out-Null
        To-Obj (Send-UEQaCommand 'QA RESETCOUNTERS')|Out-Null
        $before=Snapshot $PlayerId
        $p2Before=if($null -ne $p2){Snapshot $SecondPlayerId}else{$null}
        $inject=To-Obj (Send-UEQaCommand ("INPUT SETAXIS {0} MoveY 1.0 1200" -f $PlayerId))
        Add-Assertion 'move.p1.injected' 'input injection' $(if($inject.ok){'PASS'}else{'FAIL'}) $(if($inject.ok){'MoveY injection acknowledged by process-local bridge.'}else{'MoveY injection was rejected.'}) $inject $true
        Start-Sleep -Milliseconds 1500
        $after=Snapshot $PlayerId
        $p2After=if($null -ne $p2){Snapshot $SecondPlayerId}else{$null}
        $reads=[long]$after.input.MoveY.syntheticReads
        Add-Assertion 'move.p1.consumed' 'input consumption' $(if($reads -gt 0){'PASS'}else{'FAIL'}) ("Synthetic MoveY getter reads={0}." -f $reads) ([ordered]@{syntheticReads=$reads}) $true
        $moved=Dist $before $after
        Add-Assertion 'move.p1.world' 'world movement' $(if($moved -gt $MovementThreshold){'PASS'}else{'FAIL'}) ("Mapped P1 displacement={0:N3}; threshold={1:N3}." -f $moved,$MovementThreshold) ([ordered]@{distance=$moved}) $true

        if($null -ne $p2){
            $p2Drift=Dist $p2Before $p2After
            Add-Assertion 'move.p1.isolated-from-p2' 'independent control' $(if($p2Drift -le $IsolationThreshold){'PASS'}else{'FAIL'}) ("P2 drift while only P1 was injected={0:N3}; limit={1:N3}." -f $p2Drift,$IsolationThreshold) ([ordered]@{distance=$p2Drift}) $true

            To-Obj (Send-UEQaCommand 'INPUT CLEARALL')|Out-Null
            To-Obj (Send-UEQaCommand 'QA RESETCOUNTERS')|Out-Null
            $q1Before=Snapshot $PlayerId;$q2Before=Snapshot $SecondPlayerId
            $inject2=To-Obj (Send-UEQaCommand ("INPUT SETAXIS {0} MoveX 1.0 1200" -f $SecondPlayerId))
            Start-Sleep -Milliseconds 1500
            $q1After=Snapshot $PlayerId;$q2After=Snapshot $SecondPlayerId
            $p2Move=Dist $q2Before $q2After;$p1Drift=Dist $q1Before $q1After
            $p2Reads=[long]$q2After.input.MoveX.syntheticReads
            Add-Assertion 'move.p2.injected' 'input injection' $(if($inject2.ok){'PASS'}else{'FAIL'}) 'P2 MoveX injection acknowledgement.' $inject2 $true
            Add-Assertion 'move.p2.consumed' 'input consumption' $(if($p2Reads -gt 0){'PASS'}else{'FAIL'}) ("P2 synthetic MoveX reads={0}." -f $p2Reads) ([ordered]@{syntheticReads=$p2Reads}) $true
            Add-Assertion 'move.p2.world' 'world movement' $(if($p2Move -gt $MovementThreshold){'PASS'}else{'FAIL'}) ("Mapped P2 displacement={0:N3}." -f $p2Move) ([ordered]@{distance=$p2Move}) $true
            Add-Assertion 'move.p2.isolated-from-p1' 'independent control' $(if($p1Drift -le $IsolationThreshold){'PASS'}else{'FAIL'}) ("P1 drift while only P2 was injected={0:N3}." -f $p1Drift) ([ordered]@{distance=$p1Drift}) $true
        } else {
            Add-Assertion 'move.p2.independent' 'independent control' 'SKIP' 'P2 is not supported by the current one-player H2 bootstrap; no independence claim is made.' $null $false
        }

        # Use proven P1 world displacement as the punch direction. Do not invent a direction if movement failed.
        $dx=[double]$after.position.x-[double]$before.position.x
        $dz=[double]$after.position.z-[double]$before.position.z
        $mag=[Math]::Sqrt($dx*$dx+$dz*$dz)
        To-Obj (Send-UEQaCommand 'INPUT CLEARALL')|Out-Null
        To-Obj (Send-UEQaCommand 'QA RESETCOUNTERS')|Out-Null
        if($mag -le $MovementThreshold){
            Add-Assertion 'punch.prepare-target' 'punch impact' 'SKIP' 'No verified movement direction; deterministic target placement was not attempted.' $null $true
            Add-Assertion 'punch.execution' 'punch execution' 'SKIP' 'Punch execution test requires a verified mapped movement direction.' $null $true
            Add-Assertion 'punch.impact' 'punch impact' 'SKIP' 'Punch impact test requires a verified mapped movement direction.' $null $true
        } else {
            $prep=To-Obj (Send-UEQaCommand ("QA PREPAREPUNCH {0} {1} {2} {3}" -f $PlayerId,($dx.ToString('0.######',[Globalization.CultureInfo]::InvariantCulture)),($dz.ToString('0.######',[Globalization.CultureInfo]::InvariantCulture)),($PunchTargetDistance.ToString('0.###',[Globalization.CultureInfo]::InvariantCulture))))
            if($prep.ok){Add-Assertion 'punch.prepare-target' 'punch impact' 'PASS' 'A live bot was temporarily placed in the verified movement direction; cleanup will restore it.' ([ordered]@{targetInstanceId=$prep.targetInstanceId;distance=$prep.distance}) $true}
            else{Add-Assertion 'punch.prepare-target' 'punch impact' 'SKIP' $prep.reason $prep $true}

            $arm=To-Obj (Send-UEQaCommand ("QA ARMPUNCH {0}" -f $PlayerId))
            if(!$arm.ok){
                Add-Assertion 'punch.monitor' 'punch execution' 'SKIP' $arm.reason $arm $true
                Add-Assertion 'punch.execution' 'punch execution' 'SKIP' 'PlayerPunch FSM could not be armed; getter activity is not accepted as execution proof.' $null $true
                Add-Assertion 'punch.impact' 'punch impact' 'SKIP' 'No punch execution evidence was available.' $null $true
            } else {
                Add-Assertion 'punch.monitor' 'punch execution' 'PASS' 'Exact PlayerPunch FSM located on mapped actor.' ([ordered]@{state=$arm.playerPunchState}) $true
                $pinject=To-Obj (Send-UEQaCommand ("INPUT PRESS {0} punch 450" -f $PlayerId))
                Add-Assertion 'punch.injected' 'input injection' $(if($pinject.ok){'PASS'}else{'FAIL'}) 'Punch injection acknowledgement.' $pinject $true
                $pstatus=$null;$deadline=(Get-Date).AddSeconds([Math]::Max(2,[Math]::Min($TimeoutSeconds,8)))
                do {
                    Start-Sleep -Milliseconds 100
                    $pstatus=To-Obj (Send-UEQaCommand ("QA PUNCHSTATUS {0}" -f $PlayerId))
                    if($pstatus.ok -and (($pstatus.executionObserved -and ($pstatus.impactObserved -or $pstatus.impactWindowExpired -or !$prep.ok)) -or $pstatus.executionWindowExpired)){break}
                } while((Get-Date)-lt $deadline)
                if($pstatus -and $pstatus.ok){
                    Add-Assertion 'punch.consumed' 'input consumption' $(if($pstatus.syntheticConsumed){'PASS'}else{'FAIL'}) 'Synthetic punch must be returned active to gameplay code.' ([ordered]@{syntheticConsumed=$pstatus.syntheticConsumed}) $true
                    Add-Assertion 'punch.execution' 'punch execution' $(if($pstatus.executionObserved){'PASS'}else{'FAIL'}) $(if($pstatus.executionObserved){"PlayerPunch FSM transitioned to '$($pstatus.executionState)' after consumption."}else{'No correlated PlayerPunch FSM transition; general getter activity does not count.'}) $pstatus $true
                    if(!$prep.ok){
                        Add-Assertion 'punch.impact' 'punch impact' 'SKIP' 'No deterministic live target was available.' $prep $true
                    } elseif($pstatus.impactObserved){
                        Add-Assertion 'punch.impact' 'punch impact' 'PASS' $pstatus.impactEvidence $pstatus $true
                    } else {
                        Add-Assertion 'punch.impact' 'punch impact' 'FAIL' ("No reaction-state evidence after execution. Target displacement={0:N3} is intentionally insufficient by itself." -f [double]$pstatus.weakTargetDisplacement) $pstatus $true
                    }
                } else {
                    Add-Assertion 'punch.consumed' 'input consumption' 'FAIL' 'Punch status was unavailable.' $pstatus $true
                    Add-Assertion 'punch.execution' 'punch execution' 'FAIL' 'Punch status was unavailable.' $pstatus $true
                    Add-Assertion 'punch.impact' 'punch impact' 'FAIL' 'Punch status was unavailable.' $pstatus $true
                }
            }
        }
    }

    $criticalFails=@($assertions|Where-Object{$_.critical -and $_.status -eq 'FAIL'}).Count
    $criticalSkips=@($assertions|Where-Object{$_.critical -and $_.status -eq 'SKIP'}).Count
    if($criticalFails -gt 0){$status='FAIL';$exitCode=2}
    elseif($criticalSkips -gt 0){$status='SKIP';$exitCode=3}
    else{$status='PASS';$exitCode=0}
}
catch {
    Add-Assertion 'adapter.infrastructure' 'adapter' 'FAIL' $_.Exception.Message $null $true
    $status='FAIL';$exitCode=4
}
finally {
    try{$clear=To-Obj (Send-UEQaCommand 'INPUT CLEARALL');$cleanup.inputCleared=[bool]$clear.ok}catch{}
    try{$restore=To-Obj (Send-UEQaCommand 'QA CLEANUP');$cleanup.worldRestored=[bool]($restore.ok -and $restore.worldRestored)}catch{}
    if(!$cleanup.inputCleared -or !$cleanup.worldRestored){
        Add-Assertion 'cleanup.final' 'cleanup' 'FAIL' ("Final cleanup incomplete: inputCleared={0}, worldRestored={1}." -f $cleanup.inputCleared,$cleanup.worldRestored) $cleanup $true
        $status='FAIL';if($exitCode -ne 4){$exitCode=2}
    }
    $finished=[DateTime]::UtcNow
    $result=[ordered]@{
        schemaVersion='ue.qa.adapter.v1'
        test=$Test
        status=$status
        exitCode=$exitCode
        startedUtc=$started.ToString('o')
        finishedUtc=$finished.ToString('o')
        durationMs=[int]($finished-$started).TotalMilliseconds
        protocol=if($info){$info.protocol}else{$null}
        extension='qa-verification-v1'
        assertions=$assertions
        cleanup=$cleanup
    }
    Write-UEQaResult $result $resultPath
    Write-Host ("Result: {0}" -f $resultPath)
}
exit $exitCode
