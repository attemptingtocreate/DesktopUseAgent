param(
  [ValidateSet("project", "user")]
  [string]$Scope = "project",
  [string]$ProjectRoot = (Get-Location).Path,
  [string]$NodePath,
  [string]$McpEntryPath,
  [switch]$Force
)

$ErrorActionPreference = "Stop"

function Resolve-NodePath {
  if ($NodePath -and (Test-Path $NodePath)) { return (Resolve-Path $NodePath).Path }
  $cmd = Get-Command node -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $default = "C:\Program Files\nodejs\node.exe"
  if (Test-Path $default) { return $default }
  throw "Node.js 20+ not found. Install Node or pass -NodePath."
}

function Resolve-McpEntryPath {
  if ($McpEntryPath -and (Test-Path $McpEntryPath)) { return (Resolve-Path $McpEntryPath).Path }
  $installed = Join-Path $env:LOCALAPPDATA "DesktopUseAgent\current\mcp\dist\index.js"
  if (Test-Path $installed) { return (Resolve-Path $installed).Path }
  $dev = Join-Path (Split-Path -Parent $PSScriptRoot) "apps\mcp-server\dist\index.js"
  if (Test-Path $dev) { return (Resolve-Path $dev).Path }
  throw "MCP entrypoint not found. Run scripts/install.ps1 or build apps/mcp-server."
}

function Read-JsonObject([string]$Path) {
  if (-not (Test-Path $Path)) { return @{} }
  $raw = Get-Content $Path -Raw
  if ([string]::IsNullOrWhiteSpace($raw)) { return @{} }
  return ($raw | ConvertFrom-Json)
}

function Write-JsonObject([string]$Path, $Object) {
  $dir = Split-Path -Parent $Path
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  ($Object | ConvertTo-Json -Depth 8) + [Environment]::NewLine | Set-Content -Path $Path -Encoding utf8
}

$node = Resolve-NodePath
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
if (-not $config.mcpServers) {
  $config | Add-Member -NotePropertyName mcpServers -NotePropertyValue (@{}) -Force
}
$config.mcpServers.desktopuseagent = @{
  command = $node
  args = @($mcp)
}

Write-JsonObject $configPath $config
Write-Host "Updated $configPath for DesktopUseAgent MCP (scope=$Scope)."
Write-Host "Restart Cursor or reload MCP servers to apply."
