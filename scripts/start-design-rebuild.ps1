param(
    [switch]$Preview,
    [switch]$Empty,
    [switch]$NoBuild,
    [string]$DataDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repoRoot
$sdkCandidates = @()
if ($env:DOTNET_ROOT) { $sdkCandidates += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
$sdkCandidates += 'D:\tools\dotnet\dotnet.exe'
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($systemDotnet) { $sdkCandidates += $systemDotnet.Source }
$sdkPath = $sdkCandidates | Select-Object -Unique | Where-Object { (Test-Path -LiteralPath $_) -and ((& $_ --list-sdks) -match '^10\.') } | Select-Object -First 1
if (-not $sdkPath) { throw '需要 .NET 10 SDK。请安装与 global.json 一致的版本。' }
$env:DOTNET_ROOT = Split-Path $sdkPath -Parent
if (-not $env:MPV_PATH -and (Test-Path -LiteralPath 'D:\tools\mpv\mpv.exe')) { $env:MPV_PATH = 'D:\tools\mpv\mpv.exe' }
if (-not $DataDirectory) { $DataDirectory = Join-Path $repoRoot 'work\design-rebuild-data' }
$appArguments = @('--data-dir', [IO.Path]::GetFullPath($DataDirectory))
if ($Preview -or $Empty) { $appArguments += '--preview' }
if ($Empty) { $appArguments += '--empty' }
$runArguments = @('run', '--project', 'src/WallBili.Desktop', '-c', 'Release')
if ($NoBuild) { $runArguments += '--no-build' }
& $sdkPath @runArguments -- @appArguments
exit $LASTEXITCODE
