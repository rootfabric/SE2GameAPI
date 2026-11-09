param([int]$TimeoutSeconds = 45)
$ErrorActionPreference = 'Stop'
$directory = Join-Path $env:LOCALAPPDATA 'SE2GameAPI'
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$first = $null
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    try {
        $snapshot = Get-Content (Join-Path $directory 'grids.json') -Raw | ConvertFrom-Json
        $status = Get-Content (Join-Path $directory 'status.json') -Raw | ConvertFrom-Json
    } catch { Start-Sleep -Seconds 1; continue }
    if ($snapshot.state -eq 'SCAN_ERROR') { throw $snapshot.error }
    $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$snapshot.utc).TotalSeconds
    if ($snapshot.state -ne 'READY' -or $snapshot.gridCount -lt 1 -or $snapshot.deviceCount -lt 1 -or
        $age -lt 0 -or $age -gt 15 -or $snapshot.runId -ne $status.runId -or
        !(Get-Process -Id $snapshot.processId -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 1
        continue
    }
    if (@($snapshot.grids).Count -ne $snapshot.gridCount) { throw 'Grid count mismatch.' }
    if (@($snapshot.grids.id | Select-Object -Unique).Count -ne $snapshot.gridCount) { throw 'Duplicate grid IDs.' }
    $deviceTotal = 0
    foreach ($grid in $snapshot.grids) {
        if (@($grid.devices).Count -ne $grid.deviceCount) { throw "Device count mismatch for $($grid.id)." }
        if (@($grid.devices.id | Select-Object -Unique).Count -ne $grid.deviceCount) { throw "Duplicate device IDs in $($grid.id)." }
        $blockTotal = ($grid.blockTypes.psobject.Properties.Value | Measure-Object -Sum).Sum
        if ($blockTotal -ne $grid.blockCount) { throw "Block composition mismatch for $($grid.id)." }
        foreach ($device in $grid.devices) {
            if (!$device.id -or !$device.type -or !$device.definitionId -or @($device.components).Count -eq 0) {
                throw "Incomplete device record in $($grid.id)."
            }
        }
        $deviceTotal += $grid.deviceCount
    }
    if ($deviceTotal -ne $snapshot.deviceCount) { throw 'Total device count mismatch.' }
    if ($first -and $snapshot.runId -eq $first.runId -and $snapshot.sessionGeneration -eq $first.sessionGeneration -and
        [DateTimeOffset]$snapshot.utc -gt [DateTimeOffset]$first.utc) {
        Write-Output "PASS: two fresh scans, $($snapshot.gridCount) grids, $deviceTotal devices; composition and IDs consistent; run=$($snapshot.runId)"
        exit 0
    }
    $first = $snapshot
    Start-Sleep -Seconds 1
}
throw 'No two fresh non-empty grid/device snapshots. Load a local world and inspect grids.json and game log.'
