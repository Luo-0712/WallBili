param([string]$Destination = (Join-Path $HOME '.agents\skills\wallbili-mcp'))
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\skills\wallbili-mcp'
if (-not (Test-Path $source)) { throw "Skill source not found: $source" }
New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $Destination -Recurse -Force
Write-Host "Installed the WallBili skill to $Destination"
Write-Host 'Restart the assistant session so it reloads the skill.'
