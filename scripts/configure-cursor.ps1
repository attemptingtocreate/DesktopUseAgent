param(
  [ValidateSet("project", "user")]
  [string]$Scope = "project",
  [string]$ProjectRoot = (Get-Location).Path,
  [string]$NodePath,
  [string]$McpEntryPath,
  [switch]$Force,
  # Default: on for user scope (all Cursor workspaces); off for project unless passed.
  [switch]$InstallSkill,
  [switch]$SkipSkill
)

$ErrorActionPreference = "Stop"

# Recommended for "all Cursor workspaces": -Scope user
# Project scope writes only under this repo's .cursor/mcp.json.

function Resolve-NodePath {
  if ($NodePath -and (Test-Path $NodePath)) { return (Resolve-Path $NodePath).Path }
  $cmd = Get-Command node -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $default = "C:\Program Files\nodejs\node.exe"
  if (Test-Path $default) { return $default }
  throw "Node.js 20+ not found. Install Node or pass -NodePath."
}

function Get-NodeMajorVersion([string]$ExePath) {
  try {
    $out = & $ExePath --version 2>$null
    if ($out -match 'v?(\d+)') { return [int]$Matches[1] }
  } catch { }
  return $null
}

function Resolve-McpEntryPath {
  if ($McpEntryPath -and (Test-Path $McpEntryPath)) { return (Resolve-Path $McpEntryPath).Path }
  $installed = Join-Path $env:LOCALAPPDATA "DesktopUseAgent\current\mcp\dist\index.js"
  if (Test-Path $installed) { return (Resolve-Path $installed).Path }
  $dev = Join-Path (Split-Path -Parent $PSScriptRoot) "apps\mcp-server\dist\index.js"
  if (Test-Path $dev) { return (Resolve-Path $dev).Path }
  throw "MCP entrypoint not found. Run scripts/install.ps1 or build apps/mcp-server."
}

function Resolve-SkillTemplatePath {
  $candidates = @(
    (Join-Path (Split-Path -Parent $PSScriptRoot) ".cursor\skills\desktopuseagent\SKILL.md"),
    (Join-Path $PSScriptRoot ".cursor\skills\desktopuseagent\SKILL.md"),
    (Join-Path $PSScriptRoot "cursor-skill\SKILL.md"),
    (Join-Path $PSScriptRoot "scripts\cursor-skill\SKILL.md"),
    (Join-Path (Split-Path -Parent $PSScriptRoot) "mcp\cursor-skill\SKILL.md"),
    (Join-Path $PSScriptRoot "..\mcp\cursor-skill\SKILL.md"),
    (Join-Path $env:LOCALAPPDATA "DesktopUseAgent\current\mcp\cursor-skill\SKILL.md"),
    (Join-Path $env:LOCALAPPDATA "DesktopUseAgent\current\scripts\cursor-skill\SKILL.md")
  )
  foreach ($c in $candidates) {
    try {
      $full = [System.IO.Path]::GetFullPath($c)
      if (Test-Path $full) { return $full }
    } catch { }
  }
  return $null
}

function ConvertTo-Hashtable($Value) {
  if ($null -eq $Value) { return $null }
  if ($Value -is [hashtable]) { return $Value }
  if ($Value -is [System.Collections.IDictionary]) {
    $ht = @{}
    foreach ($key in $Value.Keys) { $ht[$key] = ConvertTo-Hashtable $Value[$key] }
    return $ht
  }
  if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
    $list = @()
    foreach ($item in $Value) { $list += ,(ConvertTo-Hashtable $item) }
    return $list
  }
  if ($Value -is [PSCustomObject]) {
    $ht = @{}
    foreach ($prop in $Value.PSObject.Properties) {
      $ht[$prop.Name] = ConvertTo-Hashtable $prop.Value
    }
    return $ht
  }
  return $Value
}

function Read-JsonObject([string]$Path) {
  if (-not (Test-Path $Path)) { return @{} }
  $raw = Get-Content $Path -Raw
  if ([string]::IsNullOrWhiteSpace($raw)) { return @{} }
  return (ConvertTo-Hashtable ($raw | ConvertFrom-Json))
}

function Write-JsonObject([string]$Path, $Object) {
  $dir = Split-Path -Parent $Path
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  ($Object | ConvertTo-Json -Depth 8) + [Environment]::NewLine | Set-Content -Path $Path -Encoding utf8
}

$shouldInstallSkill = -not $SkipSkill.IsPresent -and (
  $InstallSkill.IsPresent -or ($Scope -eq "user")
)

$node = Resolve-NodePath
$major = Get-NodeMajorVersion $node
if ($null -eq $major) {
  Write-Warning "Could not determine Node.js major version for '$node'. DesktopUseAgent requires Node 20+."
} elseif ($major -lt 20) {
  throw "Node.js major version $major is too old (found '$node'). Install Node.js 20+ or pass -NodePath."
}

$mcp = Resolve-McpEntryPath
$configPath = if ($Scope -eq "user") {
  Join-Path $env:USERPROFILE ".cursor\mcp.json"
} else {
  Join-Path $ProjectRoot ".cursor\mcp.json"
}

if ((Test-Path $configPath) -and -not $Force) {
  $backup = "$configPath.bak-$(Get-Date -Format yyyyMMddHHmmss)"
  Copy-Item $configPath $backup -Force
  Write-Host "Backed up existing config to $backup"
}

$config = Read-JsonObject $configPath
if ($null -eq $config) { $config = @{} }
if ($config -isnot [hashtable]) { $config = ConvertTo-Hashtable $config }
if ($null -eq $config) { $config = @{} }

$servers = @{}
if ($config.ContainsKey("mcpServers") -and $null -ne $config["mcpServers"]) {
  $existing = $config["mcpServers"]
  if ($existing -is [hashtable]) {
    foreach ($key in $existing.Keys) { $servers[$key] = $existing[$key] }
  } elseif ($existing -is [PSCustomObject]) {
    foreach ($prop in $existing.PSObject.Properties) { $servers[$prop.Name] = ConvertTo-Hashtable $prop.Value }
  }
}
$servers["desktopuseagent"] = @{
  command = $node
  args = @($mcp)
}
$config["mcpServers"] = $servers

Write-JsonObject $configPath $config
Write-Host "Updated $configPath for DesktopUseAgent MCP (scope=$Scope)."

if ($shouldInstallSkill) {
  $template = Resolve-SkillTemplatePath
  if (-not $template) {
    Write-Warning "Skill template not found; skipped skill install. Re-run from repo or a release that includes cursor-skill."
  } else {
    $skillDest = if ($Scope -eq "user") {
      Join-Path $env:USERPROFILE ".cursor\skills\desktopuseagent\SKILL.md"
    } else {
      Join-Path $ProjectRoot ".cursor\skills\desktopuseagent\SKILL.md"
    }
    $skillDir = Split-Path -Parent $skillDest
    if (-not (Test-Path $skillDir)) { New-Item -ItemType Directory -Path $skillDir -Force | Out-Null }
    Copy-Item $template $skillDest -Force
    Write-Host "Installed skill to $skillDest"
  }
} elseif ($SkipSkill.IsPresent) {
  Write-Host "Skipped skill install (-SkipSkill)."
} else {
  Write-Host "Skipped skill install (pass -InstallSkill for project scope)."
}

Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Restart Cursor (or reload MCP servers)."
Write-Host "  2. Enable the 'desktopuseagent' MCP server in Cursor Settings → MCP."
Write-Host "  3. Open DesktopUseAgent Control Center so the Windows Agent is running."
Write-Host "  Tip: -Scope user is recommended so MCP is available in all Cursor workspaces."
Write-Host "       Cursor agents need no OpenAI/Anthropic API key for desktop tools (Mode B)."
