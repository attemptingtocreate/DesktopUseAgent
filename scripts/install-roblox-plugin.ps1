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

$port = 18374
if ($env:ROBLOX_BRIDGE_PORT) {
  $parsed = 0
  if ([int]::TryParse($env:ROBLOX_BRIDGE_PORT, [ref]$parsed) -and $parsed -gt 1024 -and $parsed -le 65535) {
    $port = $parsed
  }
}

$token = ""
if (Test-Path $tokenPath) {
  Add-Type -AssemblyName System.Security
  $protected = [System.IO.File]::ReadAllBytes($tokenPath)
  $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect($protected, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
  $token = [System.Text.Encoding]::UTF8.GetString($bytes)
} else {
  Write-Warning "Bridge token not found at $tokenPath. Start DesktopUseAgent once, then rerun this script."
}

$env:DESKTOPUSEAGENT_ROBLOX_BRIDGE_TOKEN = $token
& (Join-Path $root "scripts\build-roblox-plugin.ps1")
Remove-Item Env:DESKTOPUSEAGENT_ROBLOX_BRIDGE_TOKEN -ErrorAction SilentlyContinue

$builtArtifact = Join-Path $root "artifacts\roblox-studio\DesktopUseAgent.rbxmx"
if (-not (Test-Path $builtArtifact)) {
  throw "Built plugin artifact missing at $builtArtifact"
}

Copy-Item $builtArtifact $artifactPath -Force

try {
  $acl = Get-Acl $artifactPath
  $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    [System.Security.Principal.WindowsIdentity]::GetCurrent().Name,
    "Read",
    "Allow"
  )
  $acl.SetAccessRuleProtection($true, $false)
  $acl.ResetAccessRule($rule)
  Set-Acl $artifactPath $acl
} catch {
  Write-Warning "Could not apply restrictive ACL to plugin artifact: $($_.Exception.Message)"
}

@{
  host = "127.0.0.1"
  port = $port
  pluginArtifact = $artifactPath
  tokenStoredInArtifact = -not [string]::IsNullOrWhiteSpace($token)
} | ConvertTo-Json | Set-Content -Path $operatorConfigPath -Encoding utf8

Write-Host "Installed plugin model to $artifactPath"
Write-Host "Operator metadata written to $operatorConfigPath (no token)."
Write-Host "Restart Roblox Studio, enable HttpService, then click 'Enable Agent Bridge' in the DesktopUseAgent toolbar."
