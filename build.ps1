param([string]$Game2 = 'D:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2')
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $PSScriptRoot 'artifacts\tools\dotnet\dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = 'dotnet' }
& $dotnet build (Join-Path $PSScriptRoot 'src\SE2GameAPI.csproj') -c Release "-p:Game2=$Game2" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$output = Join-Path $PSScriptRoot 'artifacts\plugin'
New-Item -ItemType Directory $output -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'src\bin\Release\net10.0\*.dll') $output
