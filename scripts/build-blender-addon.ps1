$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $root "plugins\blender\desktopuseagent_blender"
$artifactDir = Join-Path $root "artifacts\blender"
$stagingDir = Join-Path $artifactDir "desktopuseagent_blender"
$zipPath = Join-Path $artifactDir "desktopuseagent_blender.zip"

if (-not (Test-Path $sourceDir)) {
  throw "Add-on source folder not found at $sourceDir"
}

New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
Copy-Item $sourceDir $stagingDir -Recurse -Force

$port = 18375
if ($env:BLENDER_BRIDGE_PORT) {
  $parsed = 0
  if ([int]::TryParse($env:BLENDER_BRIDGE_PORT, [ref]$parsed) -and $parsed -gt 1024 -and $parsed -le 65535) {
    $port = $parsed
  }
}

$token = ""
if ($env:DESKTOPUSEAGENT_BLENDER_BRIDGE_TOKEN) {
  $token = $env:DESKTOPUSEAGENT_BLENDER_BRIDGE_TOKEN
}

$configPath = Join-Path $stagingDir "config.py"
$tokenLiteral = if ($token) { '"' + $token + '"' } else { '""' }
@(
  "# Baked at install/build time. Token is never logged by the add-on.",
  'HOST = "127.0.0.1"',
  "PORT = $port",
  "TOKEN = $tokenLiteral"
) | Set-Content -Path $configPath -Encoding UTF8

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $stagingDir -DestinationPath $zipPath -Force

Write-Host "Built add-on artifact at $zipPath"
