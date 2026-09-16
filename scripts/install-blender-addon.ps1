$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$localAppData = $env:LOCALAPPDATA
$agentDir = Join-Path $localAppData "DesktopUseAgent"
$secretsDir = Join-Path $agentDir "secrets"
$tokenPath = Join-Path $secretsDir "blender-bridge-token.dpapi"

$blenderScripts = @(
  (Join-Path $env:APPDATA "Blender Foundation\Blender"),
  (Join-Path $env:APPDATA "Blender Foundation\Blender Foundation")
)

$token = ""
if (Test-Path $tokenPath) {
  Add-Type -AssemblyName System.Security
  $protected = [System.IO.File]::ReadAllBytes($tokenPath)
  $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect($protected, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
  $token = [System.Text.Encoding]::UTF8.GetString($bytes)
} else {
  Write-Warning "Bridge token not found at $tokenPath. Start DesktopUseAgent once, then rerun this script."
}

$env:DESKTOPUSEAGENT_BLENDER_BRIDGE_TOKEN = $token
& (Join-Path $root "scripts\build-blender-addon.ps1")
Remove-Item Env:DESKTOPUSEAGENT_BLENDER_BRIDGE_TOKEN -ErrorAction SilentlyContinue

$builtZip = Join-Path $root "artifacts\blender\desktopuseagent_blender.zip"
if (-not (Test-Path $builtZip)) {
  throw "Built add-on artifact missing at $builtZip"
}

$installed = $false
foreach ($base in $blenderScripts) {
  if (-not (Test-Path $base)) { continue }
  Get-ChildItem $base -Directory | ForEach-Object {
    $addonsDir = Join-Path $_.FullName "scripts\addons"
    if (-not (Test-Path $addonsDir)) {
      New-Item -ItemType Directory -Force -Path $addonsDir | Out-Null
    }
    $dest = Join-Path $addonsDir "desktopuseagent_blender"
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Expand-Archive -Path $builtZip -DestinationPath $addonsDir -Force
    Write-Host "Installed add-on to $dest"
    $installed = $true
  }
}

if (-not $installed) {
  Write-Warning "No Blender scripts/addons directory found. Extract $builtZip manually into your Blender addons folder."
}

Write-Host "Enable the add-on in Blender Preferences, then opt in from the DesktopUseAgent sidebar panel."
