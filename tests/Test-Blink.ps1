param([int]$TimeoutSeconds = 15)
$ErrorActionPreference = 'Stop'
$path = Join-Path $env:LOCALAPPDATA 'SE2GameAPI\blink-status.json'
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$phases = @{}
$run = $null
$ids = $null
$transitions = 0
$lastPhase = $null
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    try { $s = Get-Content $path -Raw | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 250; continue }
    $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$s.utc).TotalSeconds
    if ($age -gt 3 -or $age -lt 0 -or !(Get-Process -Id $s.processId -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 250; continue
    }
    if ($s.state -ne 'BLINKING') { throw "Blinker state: $($s.state); $($s.error)" }
    $identity = "$($s.runId):$($s.sessionGeneration)"
    $currentIds = @($s.lamps.id | Sort-Object) -join ','
    if (!$run) { $run = $identity; $ids = $currentIds }
    if ($identity -ne $run -or $ids -ne $currentIds) { throw 'Session or target lamps changed during test.' }
    if (@($s.lamps).Count -eq 0) { throw 'No lamps selected.' }
    if (@($s.lamps | Where-Object {$_.enabled -ne $s.requestedEnabled}).Count) { throw 'Enabled state does not match requested phase.' }
    # Working can update a tick after Enabled. Require both to settle in both phases.
    if (@($s.lamps | Where-Object {$_.working -ne $s.requestedEnabled}).Count -eq 0) {
        $phase = [string]$s.requestedEnabled
        $phases[$phase] = $true
        if ($null -ne $lastPhase -and $phase -ne $lastPhase) { $transitions++ }
        $lastPhase = $phase
    }
    if ($phases.Count -eq 2 -and $transitions -ge 3) {
        Write-Output "PASS: $(@($s.lamps).Count) lamps, 3 transitions; Enabled and Working confirmed ON/OFF; run=$run"
        exit 0
    }
    Start-Sleep -Milliseconds 250
}
throw 'Did not observe three confirmed light transitions. Check power, grid lights and blink-status.json.'
