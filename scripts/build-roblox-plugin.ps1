$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$sourceDir = Join-Path $root "plugins\roblox-studio\DesktopUseAgent"
$packProject = Join-Path $root "plugins\roblox-studio\build\PackPlugin.csproj"
$artifactDir = Join-Path $root "artifacts\roblox-studio"
$artifactPath = Join-Path $artifactDir "DesktopUseAgent.rbxmx"

if (-not (Test-Path $sourceDir)) {
  throw "Plugin source folder not found at $sourceDir"
}

New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

$port = 18374
if ($env:ROBLOX_BRIDGE_PORT) {
  $parsed = 0
  if ([int]::TryParse($env:ROBLOX_BRIDGE_PORT, [ref]$parsed) -and $parsed -gt 1024 -and $parsed -le 65535) {
    $port = $parsed
  }
}

$token = ""
if ($env:DESKTOPUSEAGENT_ROBLOX_BRIDGE_TOKEN) {
  $token = $env:DESKTOPUSEAGENT_ROBLOX_BRIDGE_TOKEN
}

& $dotnet run --project $packProject -c Release -- `
  --source $sourceDir `
  --output $artifactPath `
  --host 127.0.0.1 `
  --port $port `
  --token $token

if ($LASTEXITCODE -ne 0) {
  throw "Roblox plugin pack failed with exit code $LASTEXITCODE."
}

Write-Host "Built plugin artifact at $artifactPath"
