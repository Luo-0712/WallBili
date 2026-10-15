$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
New-Item -ItemType Directory -Force work | Out-Null
$mpvExecutable = if ($env:MPV_PATH) { $env:MPV_PATH } else { 'mpv' }
$ffmpegExecutable = if ($env:FFMPEG_PATH) { $env:FFMPEG_PATH } else { 'ffmpeg' }
Get-Command $mpvExecutable -ErrorAction Stop | Out-Null
Get-Command $ffmpegExecutable -ErrorAction Stop | Out-Null

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Invoke-Dotnet restore WallBili.slnx --locked-mode --disable-parallel -m:1
Invoke-Dotnet build WallBili.slnx -c Release --no-restore -m:1
Invoke-Dotnet run --project tests/WallBili.Infrastructure.Tests -c Release --no-build -- -result-xml work/content-tests.xml -nologo
Invoke-Dotnet run --project tests/WallBili.Player.Mpv.Tests -c Release --no-build -- -result-xml work/player-tests.xml -nologo
Invoke-Dotnet run --project tests/WallBili.Library.Tests -c Release --no-build -- -result-xml work/library-tests.xml -nologo
