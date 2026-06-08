# Dev convenience: kill any prior DevTree, rebuild Research, and launch the explorer by running the
# built EXE directly (NOT `dotnet run`, which spawns an apphost child that lingers and locks the
# output on the next rebuild). Ctrl+C stops it cleanly.
param([int]$Port = 7778)
$ErrorActionPreference = 'Stop'
Get-Process -Name POE2Radar.Research -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
$proj = Join-Path $PSScriptRoot 'src/POE2Radar.Research/POE2Radar.Research.csproj'
dotnet build $proj -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error 'build failed'; exit 1 }
$exe = Join-Path $PSScriptRoot 'src/POE2Radar.Research/bin/Debug/net10.0-windows/POE2Radar.Research.exe'
& $exe --devtree --port $Port
