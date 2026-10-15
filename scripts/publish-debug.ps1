$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
& dotnet publish src/WallBili.Desktop -c Release -r win-x64 --self-contained false -o work/windows-debug -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }
Write-Host 'Windows debug app: work/windows-debug/WallBili.Desktop.exe'
Write-Host 'Requires .NET 10 Desktop Runtime and mpv 0.41.0 (set MPV_PATH or use the path field).'
