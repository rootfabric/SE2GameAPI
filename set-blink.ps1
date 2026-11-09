param(
    [ValidateSet('start','stop')][string]$Action = 'start',
    [string]$GridName = 'Concordia Research Station',
    [string]$LampName = 'Interior Light 0.5 m',
    [ValidateRange(500,60000)][int]$IntervalMs = 1000,
    [ValidateRange(1,100)][int]$ExpectedCount = 3
)
$ErrorActionPreference = 'Stop'
$directory = Join-Path $env:LOCALAPPDATA 'SE2GameAPI'
New-Item -ItemType Directory $directory -Force | Out-Null
$path = Join-Path $directory 'blink.json'
@{ enabled = ($Action -eq 'start'); gridName = $GridName; lampName = $LampName;
   intervalMs = $IntervalMs; expectedCount = $ExpectedCount } |
    ConvertTo-Json | Set-Content "$path.tmp" -Encoding utf8
Move-Item -LiteralPath "$path.tmp" -Destination $path -Force
Write-Output "Blink request: $Action. Check $directory\blink-status.json for confirmation."
