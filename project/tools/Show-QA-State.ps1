$state = Join-Path $env:LOCALAPPDATA 'UnspottableExpanded\QA\state.json'
if (-not (Test-Path $state)) { Write-Host 'No QA state file exists yet.'; exit 1 }
Get-Content -LiteralPath $state -Raw | ConvertFrom-Json | Format-List
