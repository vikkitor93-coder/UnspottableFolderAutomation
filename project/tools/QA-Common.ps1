Set-StrictMode -Version 2

function Send-UEQaCommand {
    param([Parameter(Mandatory=$true)][string]$Line,[int]$TimeoutMs=4000)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.ReceiveTimeout = $TimeoutMs
        $client.SendTimeout = $TimeoutMs
        $client.Connect('127.0.0.1',24783)
        $stream = $client.GetStream()
        $writer = New-Object System.IO.StreamWriter($stream,(New-Object System.Text.UTF8Encoding($false)),1024,$true)
        $reader = New-Object System.IO.StreamReader($stream,[Text.Encoding]::UTF8,$false,1024,$true)
        $writer.AutoFlush = $true
        $writer.WriteLine($Line)
        return $reader.ReadLine()
    }
    finally { if($client){$client.Dispose()} }
}

function ConvertTo-UEQaSafeState {
    param($State)
    if($null -eq $State){ return $null }
    $players = @()
    foreach($p in @($State.players)){
        if($null -eq $p){continue}
        $players += [ordered]@{
            instanceId = $p.instanceId
            active = $p.active
            x = $p.x; y = $p.y; z = $p.z
        }
    }
    return [ordered]@{
        pluginVersion = $State.pluginVersion
        heartbeat = $State.heartbeat
        utc = $State.utc
        scene = $State.scene
        sceneBuildIndex = $State.sceneBuildIndex
        qaMode = $State.qaMode
        headlessRequested = $State.headlessRequested
        isBatchMode = $State.isBatchMode
        graphicsDevice = $State.graphicsDevice # API family only in v0.9.8, never the hardware model.
        runInBackground = $State.runInBackground
        targetFps = $State.targetFps
        qaInputEnabled = $State.qaInputEnabled
        qaInputPatched = $State.qaInputPatched
        qaInputPatchCount = $State.qaInputPatchCount
        qaInputInterceptCount = $State.qaInputInterceptCount
        qaGameplayEnabled = $State.qaGameplayEnabled
        qaGameplayStage = $State.qaGameplayStage
        qaGameplayReady = $State.qaGameplayReady
        qaGameplayFailed = $State.qaGameplayFailed
        qaGameplayMessage = $State.qaGameplayMessage
        rewiredReady = $State.rewiredReady
        rewiredPlayerCount = $State.rewiredPlayerCount
        playerCount = $State.playerCount
        botCount = $State.botCount
        players = $players
    }
}

function Copy-UEQaSafeState {
    param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Destination)
    if(!(Test-Path -LiteralPath $Source)){return}
    try {
        $raw = Get-Content -LiteralPath $Source -Raw | ConvertFrom-Json
        ConvertTo-UEQaSafeState $raw | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Destination -Encoding UTF8
    } catch {}
}

function Copy-UEQaSafeMetadata {
    param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Destination)
    if(!(Test-Path -LiteralPath $Source)){return}
    try {
        $m = Get-Content -LiteralPath $Source -Raw | ConvertFrom-Json
        $safePlayers = @()
        foreach($p in @($m.players)){
            if($null -eq $p){continue}
            $safePlayers += [ordered]@{id=$p.id;isPlaying=$p.isPlaying}
        }
        [ordered]@{rewiredVersion=$m.rewiredVersion;playerCount=$m.playerCount;players=$safePlayers;actions=@($m.actions)} |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Destination -Encoding UTF8
    } catch {}
}

function Copy-UEQaSanitizedText {
    param(
        [Parameter(Mandatory=$true)][string]$Source,
        [Parameter(Mandatory=$true)][string]$Destination,
        [switch]$QaLinesOnly
    )
    if(!(Test-Path -LiteralPath $Source)){return}
    $lines = Get-Content -LiteralPath $Source -ErrorAction SilentlyContinue
    $safe = New-Object System.Collections.Generic.List[string]
    foreach($line in @($lines)){
        if($QaLinesOnly -and $line -notmatch 'Unspottable Expanded|\bQA\b|Chainloader startup complete|Scene loaded:'){continue}
        $x = [string]$line
        $x = $x -replace '(?i)[A-Z]:\\Users\\[^\\\s]+','<USER_PROFILE>'
        $x = $x -replace '(?i)[A-Z]:\\[^\r\n"'']+','<LOCAL_PATH>'
        $x = $x -replace '(?i)(Bearer\s+)[A-Za-z0-9._~+\-/=]+','$1<REDACTED>'
        $x = $x -replace '(?i)((api[_-]?key|token|secret|password)\s*[:=]\s*)\S+','$1<REDACTED>'
        $x = $x -replace '(?<![0-9])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9])','<NETWORK_ADDRESS>'
        $safe.Add($x) | Out-Null
    }
    $safe | Set-Content -LiteralPath $Destination -Encoding UTF8
}

function Write-UEQaResult {
    param([Parameter(Mandatory=$true)]$Result,[Parameter(Mandatory=$true)][string]$Path)
    $Result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding UTF8
}
