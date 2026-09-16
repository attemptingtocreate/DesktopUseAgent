$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$localAppData = $env:LOCALAPPDATA
$pluginsDir = Join-Path $localAppData "Roblox\Plugins"
$agentDir = Join-Path $localAppData "DesktopUseAgent"
$secretsDir = Join-Path $agentDir "secrets"
$tokenPath = Join-Path $secretsDir "roblox-bridge-token.dpapi"
$artifactPath = Join-Path $pluginsDir "DesktopUseAgent.rbxmx"
$operatorConfigPath = Join-Path $agentDir "roblox-plugin-config.json"

New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
New-Item -ItemType Directory -Force -Path $secretsDir | Out-Null

$port = 18374
if ($env:ROBLOX_BRIDGE_PORT) {
  $parsed = 0
  if ([int]::TryParse($env:ROBLOX_BRIDGE_PORT, [ref]$parsed) -and $parsed -gt 1024 -and $parsed -le 65535) {
    $port = $parsed
  }
}

function Grant-OwnerFullControl {
  param([string]$Path)
  if (-not (Test-Path $Path)) { return }
  try {
    $acl = Get-Acl $Path
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
      $identity,
      "FullControl",
      "Allow"
    )
    $acl.SetAccessRuleProtection($true, $false)
    $acl.ResetAccessRule($rule)
    Set-Acl -Path $Path -AclObject $acl
  } catch {
    Write-Warning "Could not reset ACL on $Path : $($_.Exception.Message)"
  }
}

Add-Type -AssemblyName System.Security
$token = ""
if (Test-Path $tokenPath) {
  $protected = [System.IO.File]::ReadAllBytes($tokenPath)
  $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect($protected, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
  $token = [System.Text.Encoding]::UTF8.GetString($bytes)
}

if ([string]::IsNullOrWhiteSpace($token)) {
  # Create a DPAPI token the agent will reuse on next start (same path/format as RobloxBridgeTokenStore).
  $tokenBytes = New-Object byte[] 32
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($tokenBytes)
  $token = [Convert]::ToBase64String($tokenBytes)
  $plain = [System.Text.Encoding]::UTF8.GetBytes($token)
  $protectedOut = [System.Security.Cryptography.ProtectedData]::Protect($plain, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
  [System.IO.File]::WriteAllBytes($tokenPath, $protectedOut)
  Write-Host "Created Roblox bridge token at $tokenPath"
}

$env:DESKTOPUSEAGENT_ROBLOX_BRIDGE_TOKEN = $token
& (Join-Path $root "scripts\build-roblox-plugin.ps1")
Remove-Item Env:DESKTOPUSEAGENT_ROBLOX_BRIDGE_TOKEN -ErrorAction SilentlyContinue

$builtArtifact = Join-Path $root "artifacts\roblox-studio\DesktopUseAgent.rbxmx"
if (-not (Test-Path $builtArtifact)) {
  throw "Built plugin artifact missing at $builtArtifact"
}

# Previous installs applied a Read-only ACL that blocked updates; clear it first.
if (Test-Path $artifactPath) {
  Grant-OwnerFullControl -Path $artifactPath
  try {
    Remove-Item $artifactPath -Force
  } catch {
    throw @"
Access denied removing '$artifactPath'.
Close Roblox Studio (it locks the plugin file), then rerun:
  .\scripts\install-roblox-plugin.ps1
$($_.Exception.Message)
"@
  }
}

try {
  Copy-Item $builtArtifact $artifactPath -Force
} catch {
  throw @"
Access denied installing to '$artifactPath'.
Close Roblox Studio, then rerun this script.
$($_.Exception.Message)
"@
}

try {
  # Owner needs FullControl so future reinstalls can overwrite; others get nothing.
  Grant-OwnerFullControl -Path $artifactPath
} catch {
  Write-Warning "Could not apply restrictive ACL to plugin artifact: $($_.Exception.Message)"
}

@{
  host = "127.0.0.1"
  port = $port
  pluginArtifact = $artifactPath
  tokenStoredInArtifact = $true
} | ConvertTo-Json | Set-Content -Path $operatorConfigPath -Encoding utf8

Write-Host "Installed plugin model to $artifactPath"
Write-Host "Operator metadata written to $operatorConfigPath (no token)."
Write-Host "Restart Roblox Studio, enable HttpService, then click 'Enable Agent Bridge' in the DesktopUseAgent toolbar."
Write-Host "If the Windows agent was already running, restart DesktopUseAgent so it loads the same bridge token."
