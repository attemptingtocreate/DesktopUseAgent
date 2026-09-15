param(
  [string]$ProfileName = "desktopuseagent",
  [string]$ProfileDir,
  [string]$TunnelClientPath
)

$ErrorActionPreference = "Stop"

function Resolve-TunnelClient {
  param([string]$ExplicitPath)

  if ($ExplicitPath) {
    if (-not (Test-Path $ExplicitPath)) {
      throw "TunnelClientPath not found: $ExplicitPath"
    }
    return (Resolve-Path $ExplicitPath).Path
  }

  $stableExe = Join-Path $env:LOCALAPPDATA "DesktopUseAgent\tools\tunnel-client\current\tunnel-client.exe"
  if (Test-Path $stableExe) {
    return (Resolve-Path $stableExe).Path
  }

  $installed = Get-ChildItem (Join-Path $env:LOCALAPPDATA "DesktopUseAgent\tools\tunnel-client") -Recurse -Filter "tunnel-client.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if ($installed) {
    return $installed.FullName
  }

  $cmd = Get-Command tunnel-client -ErrorAction SilentlyContinue
  if ($cmd) {
    return $cmd.Source
  }

  throw @"
tunnel-client was not found.

Install the official client:
  .\scripts\install-openai-tunnel-client.ps1

Or pass -TunnelClientPath to an existing tunnel-client.exe.
"@
}

if ($ProfileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
  throw "ProfileName must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ (1-64 chars; start with letter or digit)."
}

if (-not $env:CONTROL_PLANE_API_KEY) {
  throw @"
CONTROL_PLANE_API_KEY is required but not set.

Create a restricted Runtime API key with Tunnels Read + Use permissions:
  https://platform.openai.com/settings/organization/api-keys

Set it for this shell only:
  `$env:CONTROL_PLANE_API_KEY = "<your-runtime-key>"

This script never prints or stores the key.
"@
}

$profileDirectory = if ($ProfileDir) { $ProfileDir } else { Join-Path $env:LOCALAPPDATA "DesktopUseAgent\tunnel-profiles" }
$profileFile = Join-Path $profileDirectory "$ProfileName.yaml"
if (-not (Test-Path $profileFile)) {
  throw @"
Profile not found: $profileFile

Generate it first:
  .\scripts\setup-chatgpt-tunnel.ps1 -TunnelId tunnel_<your-id>
"@
}

$tunnelClient = Resolve-TunnelClient -ExplicitPath $TunnelClientPath

Write-Host "Using tunnel-client: $tunnelClient"
Write-Host "Profile: $profileFile"
Write-Host ""
Write-Host "Keep DesktopUseAgent Control Center and the Windows Agent running."
Write-Host "The MCP server speaks stdio to the local named pipe semantic-desktop-agent."
Write-Host "tunnel-client provides outbound HTTPS to the OpenAI control plane."
Write-Host ""

Write-Host "Running tunnel-client doctor --explain ..."
& $tunnelClient doctor --profile $ProfileName --profile-dir $profileDirectory --explain
if ($LASTEXITCODE -ne 0) {
  throw "tunnel-client doctor failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Starting tunnel-client run (foreground). Press Ctrl+C to stop."
& $tunnelClient run --profile $ProfileName --profile-dir $profileDirectory
if ($LASTEXITCODE -ne 0) {
  throw "tunnel-client run failed with exit code $LASTEXITCODE."
}
