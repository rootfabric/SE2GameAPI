param([string]$Game2 = 'D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2', [switch]$ContinueGame)
$ErrorActionPreference = 'Stop'
$hostedPulsar = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'Modern\.dll' }
if ((Get-Process SpaceEngineers2,Modern -ErrorAction SilentlyContinue) -or $hostedPulsar) {
    throw 'Close the game/Pulsar normally before starting the smoke test.'
}
& (Join-Path $PSScriptRoot 'build.ps1') -Game2 $Game2
$pulsar = Join-Path $PSScriptRoot 'artifacts\tools\Pulsar'
$dotnet = Join-Path $PSScriptRoot 'artifacts\tools\dotnet\dotnet.exe'
if (!(Test-Path "$pulsar\Modern.dll") -or !(Test-Path $dotnet)) {
    throw 'Portable Pulsar and .NET SDK must be installed in artifacts\tools. See README.'
}
New-Item -ItemType Directory "$pulsar\Modern\Local\SE2GameAPI" -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'artifacts\plugin\*.dll') "$pulsar\Modern\Local\SE2GameAPI"
Move-Item "$pulsar\Modern\Local\SE2GameAPI\SE2GameAPI.dll" "$pulsar\Modern\Local\SE2GameAPI\plugin.dll" -Force
if (Test-Path "$pulsar\Modern\Local\SE2GameAPI.dll") {
    Move-Item "$pulsar\Modern\Local\SE2GameAPI.dll" "$pulsar\Modern\SE2GameAPI.previous.dll" -Force
}
$profile = Join-Path $PSScriptRoot 'smoke-profile.xml'
$arguments = "`"$pulsar\Modern.dll`" -game2 `"$Game2`" -profile `"$profile`" -noUpdate -noStats -noDiscord -bare"
if ($ContinueGame) { $arguments += ' -continue' }
Start-Process -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $pulsar -WindowStyle Hidden
Write-Output "Started Pulsar. Check $env:LOCALAPPDATA\SE2GameAPI\bridge-state.json or Redis se2:local:state for READY and a fresh sampledAtMs."
