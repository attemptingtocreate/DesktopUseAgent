param(
  [Parameter(Mandatory = $true)]
  [string]$TunnelId,

  [string]$ProfileName = "desktopuseagent",
  [string]$TunnelClientPath,
  [string]$McpEntryPath,
  [string]$ProfileDir,
  [switch]$OpenWebUi,
  [switch]$Force
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

function Resolve-NodeExecutable {
  $cmd = Get-Command node -ErrorAction SilentlyContinue
  if (-not $cmd) {
    throw @"
Node.js 20+ is required but 'node' was not found on PATH.

Install Node 20+ from https://nodejs.org/ and reopen your shell.
"@
  }

  $versionText = (& $cmd.Source --version).TrimStart("v")
  $major = [int]($versionText.Split(".")[0])
  if ($major -lt 20) {
    throw "Node.js 20+ is required. Found v$versionText at $($cmd.Source)."
  }

  return (Resolve-Path $cmd.Source).Path
}

function Format-McpCommandPath {
  param([string]$Path)
  $normalized = $Path.Replace("\", "/")
  if ($normalized -match '\s|"') {
    return "`"$normalized`""
  }
  return $normalized
}

function Build-McpCommand {
  param(
    [string]$NodeExe,
    [string]$McpEntry
  )
  return "$(Format-McpCommandPath $NodeExe) $(Format-McpCommandPath $McpEntry)"
}

function Resolve-McpEntry {
  param(
    [string]$ExplicitPath,
    [string]$RepoRoot
  )

  if ($ExplicitPath) {
    if (-not (Test-Path $ExplicitPath)) {
      throw "McpEntryPath not found: $ExplicitPath"
    }
    return (Resolve-Path $ExplicitPath).Path
  }

  $installed = Join-Path $env:LOCALAPPDATA "DesktopUseAgent\current\mcp\dist\index.js"
  if (Test-Path $installed) {
    return (Resolve-Path $installed).Path
  }

  $repoBuilt = Join-Path $RepoRoot "apps\mcp-server\dist\index.js"
  if (Test-Path $repoBuilt) {
    return (Resolve-Path $repoBuilt).Path
  }

  throw @"
MCP server entrypoint dist\index.js was not found.

Preferred install path:
  %LOCALAPPDATA%\DesktopUseAgent\current\mcp\dist\index.js

Build from the repo:
  cd apps\mcp-server
  npm install
  npm run build

Or pass -McpEntryPath to an existing dist\index.js.
"@
}

if ($TunnelId -notmatch '^tunnel_[0-9a-f]{32}$') {
  throw "TunnelId must match ^tunnel_[0-9a-f]{32}$ (Platform format: tunnel_ plus 32 lowercase hex digits). Example: tunnel_0123456789abcdef0123456789abcdef"
}

if ($ProfileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
  throw "ProfileName must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ (1-64 chars; start with letter or digit)."
}

$root = Split-Path -Parent $PSScriptRoot
$tunnelClient = Resolve-TunnelClient -ExplicitPath $TunnelClientPath
$nodeExe = Resolve-NodeExecutable
$mcpEntry = Resolve-McpEntry -ExplicitPath $McpEntryPath -RepoRoot $root
$profileDirectory = if ($ProfileDir) { $ProfileDir } else { Join-Path $env:LOCALAPPDATA "DesktopUseAgent\tunnel-profiles" }
New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
$profileFile = Join-Path $profileDirectory "$ProfileName.yaml"

if ((Test-Path $profileFile) -and -not $Force) {
  Write-Host "Profile already exists: $profileFile"
  Write-Host "Re-run with -Force to regenerate it."
}
else {
  $mcpCommand = Build-McpCommand -NodeExe $nodeExe -McpEntry $mcpEntry
  $initArgs = @(
    "init",
    "--sample", "sample_mcp_stdio_local",
    "--profile", $ProfileName,
    "--profile-dir", $profileDirectory,
    "--tunnel-id", $TunnelId,
    "--mcp-command", $mcpCommand
  )
  if ($Force) {
    $initArgs += "--force"
  }
  & $tunnelClient @initArgs
  if ($LASTEXITCODE -ne 0) {
    throw "tunnel-client init failed with exit code $LASTEXITCODE."
  }
  Write-Host "Created profile: $profileFile"
}

Write-Host ""
Write-Host "Local prerequisites verified:"
Write-Host "  tunnel-client: $tunnelClient"
Write-Host "  node: $nodeExe"
Write-Host "  MCP entry: $mcpEntry"
Write-Host "  profile: $profileFile"
Write-Host ""
Write-Host "Next steps (credentials are never stored by these scripts):"
Write-Host "  1. Create a tunnel in Platform Tunnels settings and confirm TunnelId matches."
Write-Host "     https://platform.openai.com/settings/organization/tunnels"
Write-Host "  2. Create a restricted Runtime API key with Tunnels Read + Use permissions."
Write-Host "     https://platform.openai.com/settings/organization/api-keys"
Write-Host "  3. In PowerShell, set the runtime key for this session only:"
Write-Host '     $env:CONTROL_PLANE_API_KEY = "<your-runtime-key>"'
Write-Host "  4. Start DesktopUseAgent Control Center and keep the Windows Agent running."
Write-Host "  5. Start the tunnel:"
Write-Host "     .\scripts\start-chatgpt-tunnel.ps1 -ProfileName $ProfileName"
Write-Host ""
Write-Host "Do not use OPENAI_ADMIN_KEY for the long-lived tunnel daemon."
Write-Host "This setup script does not accept, print, or persist API keys."

if ($OpenWebUi) {
  Write-Host ""
  Write-Host "Opening Platform tunnel settings..."
  Start-Process "https://platform.openai.com/settings/organization/tunnels"
  Write-Host "After start-chatgpt-tunnel.ps1 is running, open the local admin UI at http://127.0.0.1:<health-port>/ui"
}

if ($env:CONTROL_PLANE_API_KEY) {
  Write-Host ""
  Write-Host "CONTROL_PLANE_API_KEY is set in this shell. Running local doctor validation..."
  & $tunnelClient doctor --profile $ProfileName --profile-dir $profileDirectory --explain
  if ($LASTEXITCODE -ne 0) {
    throw "tunnel-client doctor failed with exit code $LASTEXITCODE."
  }
}
else {
  Write-Host ""
  Write-Host "Skipped tunnel-client doctor because CONTROL_PLANE_API_KEY is not set in this shell."
}
