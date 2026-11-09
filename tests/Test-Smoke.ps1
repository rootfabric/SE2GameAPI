param([int]$TimeoutSeconds = 30)
$ErrorActionPreference = 'Stop'
$statusPath = Join-Path $env:LOCALAPPDATA 'SE2GameAPI\status.json'
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$first = $null
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if (Test-Path $statusPath) {
        try { $status = Get-Content $statusPath -Raw | ConvertFrom-Json } catch { $status = $null }
        if ($status -and $status.phase -eq 'MAIN_THREAD_HEARTBEAT') {
            $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$status.utc).TotalSeconds
            $process = Get-Process -Id $status.processId -ErrorAction SilentlyContinue
            if ($process -and $age -ge 0 -and $age -lt 15) {
                if ($first -and $status.runId -eq $first.runId -and $status.heartbeats -gt $first.heartbeats) {
                    Write-Output "PASS: live game queue heartbeat advanced $($first.heartbeats) -> $($status.heartbeats); PID=$($status.processId); run=$($status.runId)"
                    exit 0
                }
                $first = $status
            }
        }
    }
    Start-Sleep -Seconds 1
}
throw "No advancing live MAIN_THREAD_HEARTBEAT within $TimeoutSeconds seconds. See $statusPath and Pulsar Modern\info.log."
