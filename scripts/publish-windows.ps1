param([string]$OutputDirectory = 'work/windows-phase3', [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$packageDirectory = (Resolve-Path $OutputDirectory).Path
$standalone = if ($SelfContained) { 'true' } else { 'false' }
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Invoke-Dotnet publish src/WallBili.Desktop -c Release -r win-x64 --self-contained $standalone -o "$packageDirectory/app" "-p:PublishSingleFile=false" "-m:1"
Invoke-Dotnet publish tools/WallBili.DesktopProbe -c Release -r win-x64 --self-contained $standalone -o "$packageDirectory/desktop-probe" "-p:PublishSingleFile=false" "-m:1"
Invoke-Dotnet publish tools/WallBili.McpBridge -c Release -r win-x64 --self-contained $standalone -o "$packageDirectory/mcp-bridge" "-p:PublishSingleFile=false" "-m:1"
Copy-Item LICENSE,THIRD-PARTY-NOTICES.md,README.md $packageDirectory
Copy-Item -Recurse -Force licenses $packageDirectory
Copy-Item docs/windows-quickstart.md,docs/windows-phase3-checklist.md,docs/phase3-validation.md $packageDirectory
Copy-Item scripts/measure-resources.ps1 $packageDirectory
Compress-Archive -Path "$packageDirectory/*" -DestinationPath "$packageDirectory.zip" -Force
Write-Host "Windows x64 package: $packageDirectory.zip"
Write-Host 'mpv 0.41.0 is installed separately. Framework-dependent packages also need .NET 10 Desktop Runtime and ASP.NET Core Runtime 10.'
Write-Host 'Self-contained packages include the .NET runtimes for the application and MCP bridge.'
