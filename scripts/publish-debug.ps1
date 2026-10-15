$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
& dotnet publish src/WallBili.Desktop -c Release -r win-x64 --self-contained false -o work/windows-debug -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }
& dotnet publish tools/WallBili.DesktopProbe -c Release -r win-x64 --self-contained false -o work/windows-debug/desktop-probe -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "Desktop probe publish failed with exit code $LASTEXITCODE" }
Copy-Item LICENSE,THIRD-PARTY-NOTICES.md work/windows-debug
Copy-Item -Recurse -Force licenses work/windows-debug
Copy-Item docs/windows-phase2-checklist.md,docs/phase2-validation.md work/windows-debug
Write-Host 'Windows debug app: work/windows-debug/WallBili.Desktop.exe'
Write-Host 'Requires .NET 10 Desktop Runtime and mpv 0.41.0 (set MPV_PATH or use the path field).'
