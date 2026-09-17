param(
  [switch]$SkipCursorConfig
)

$ErrorActionPreference = "Stop"

function Resolve-LayoutRoot {
  param([string]$ScriptRoot)
  $candidates = @(
    (Join-Path $ScriptRoot "layout"),
    (Join-Path (Split-Path -Parent $ScriptRoot) "artifacts\layout"),
    (Join-Path (Split-Path -Parent $ScriptRoot) "layout")
  )
  foreach ($candidate in $candidates) {
    if (Test-Path (Join-Path $candidate "agent")) {
      return (Resolve-Path $candidate).Path
    }
  }
  throw "Could not locate packaged layout. Run scripts/pack.ps1 or extract a release zip that includes layout/."
}

function Read-ProductVersion {
  param([string]$ScriptRoot)
  $versionFile = Join-Path (Split-Path -Parent $ScriptRoot) "VERSION"
  if (-not (Test-Path $versionFile)) {
    $versionFile = Join-Path $ScriptRoot "VERSION"
  }
  if (Test-Path $versionFile) {
    return (Get-Content $versionFile -Raw).Trim()
  }
  return "0.0.0-dev"
}

function Copy-CursorSupportFiles {
  param(
    [string]$ScriptRoot,
    [string]$Target
  )
  $scriptsDest = Join-Path $Target "scripts"
  New-Item -ItemType Directory -Path $scriptsDest -Force | Out-Null

  $configureCandidates = @(
    (Join-Path $ScriptRoot "configure-cursor.ps1"),
    (Join-Path $ScriptRoot "scripts\configure-cursor.ps1"),
    (Join-Path (Split-Path -Parent $ScriptRoot) "scripts\configure-cursor.ps1")
  )
  foreach ($src in $configureCandidates) {
    if (Test-Path $src) {
      Copy-Item $src (Join-Path $scriptsDest "configure-cursor.ps1") -Force
      break
    }
  }

  $skillDest = Join-Path $Target "mcp\cursor-skill"
  $skillCandidates = @(
    (Join-Path $Target "mcp\cursor-skill\SKILL.md"),
    (Join-Path $ScriptRoot ".cursor\skills\desktopuseagent\SKILL.md"),
    (Join-Path $ScriptRoot "scripts\cursor-skill\SKILL.md"),
    (Join-Path (Split-Path -Parent $ScriptRoot) ".cursor\skills\desktopuseagent\SKILL.md")
  )
  $skillSrc = $null
  foreach ($c in $skillCandidates) {
    if (Test-Path $c) { $skillSrc = $c; break }
  }
  if ($skillSrc -and -not (Test-Path (Join-Path $skillDest "SKILL.md"))) {
    New-Item -ItemType Directory -Path $skillDest -Force | Out-Null
    Copy-Item $skillSrc (Join-Path $skillDest "SKILL.md") -Force
  }

  $exampleDestDir = Join-Path $Target ".cursor"
  $exampleCandidates = @(
    (Join-Path $Target ".cursor\mcp.json.example"),
    (Join-Path $ScriptRoot ".cursor\mcp.json.example"),
    (Join-Path (Split-Path -Parent $ScriptRoot) ".cursor\mcp.json.example")
  )
  foreach ($ex in $exampleCandidates) {
    if (Test-Path $ex) {
      if (-not (Test-Path (Join-Path $exampleDestDir "mcp.json.example"))) {
        New-Item -ItemType Directory -Path $exampleDestDir -Force | Out-Null
        Copy-Item $ex (Join-Path $exampleDestDir "mcp.json.example") -Force
      }
      break
    }
  }
}

$ScriptRoot = $PSScriptRoot
$layout = Resolve-LayoutRoot -ScriptRoot $ScriptRoot
$version = Read-ProductVersion -ScriptRoot $ScriptRoot
$target = Join-Path $env:LOCALAPPDATA "DesktopUseAgent\current"
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item (Join-Path $layout "*") $target -Recurse
Copy-CursorSupportFiles -ScriptRoot $ScriptRoot -Target $target
$appExe = Join-Path $target "control-center\DesktopUseAgent.exe"
if (-not (Test-Path $appExe)) {
  throw "DesktopUseAgent.exe is missing from the packaged control-center layout."
}
$manifest = @{
  schemaVersion = 1
  version = $version
  channel = "developer-preview"
  minCompatibleApi = "1.0.0"
  files = @()
}
Get-ChildItem $target -Recurse -File | ForEach-Object {
  $rel = $_.FullName.Substring($target.Length).TrimStart("\").Replace("\", "/")
  $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  $manifest.files += @{ path = $rel; sha256 = $hash }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $target "install-manifest.json") -Encoding utf8
$programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcut = Join-Path $programs "DesktopUseAgent.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $appExe
$link.WorkingDirectory = Split-Path $appExe -Parent
$link.IconLocation = "$appExe,0"
$link.Description = "DesktopUseAgent"
$link.Save()
Write-Host "Installed DesktopUseAgent $version to $target"
Write-Host "Start Menu shortcut created at $shortcut"

$cursorReady = $false
$cursorNote = ""
if ($SkipCursorConfig) {
  $cursorNote = "Skipped Cursor MCP config (-SkipCursorConfig)."
} else {
  $configure = $null
  foreach ($c in @(
    (Join-Path $target "scripts\configure-cursor.ps1"),
    (Join-Path $ScriptRoot "configure-cursor.ps1"),
    (Join-Path $ScriptRoot "scripts\configure-cursor.ps1"),
    (Join-Path (Split-Path -Parent $ScriptRoot) "scripts\configure-cursor.ps1")
  )) {
    if (Test-Path $c) { $configure = $c; break }
  }
  if (-not $configure) {
    $cursorNote = "WARN: configure-cursor.ps1 not found; configure Cursor manually (docs/cursor-setup.md)."
  } else {
    try {
      & $configure -Scope user
      if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        $cursorNote = "WARN: configure-cursor.ps1 exited with code $LASTEXITCODE (install succeeded)."
      } else {
        $cursorReady = $true
        $cursorNote = "Cursor MCP configured (user scope + skill)."
      }
    } catch {
      $cursorNote = "WARN: Cursor MCP config failed: $($_.Exception.Message) (install succeeded; Node 20+ and MCP required)."
    }
  }
}

Write-Host ""
Write-Host "Cursor readiness: $(if ($cursorReady) { 'READY' } else { 'NEEDS ATTENTION' })"
Write-Host "  $cursorNote"
Write-Host "  No OpenAI/Anthropic API key needed for Cursor agents — Mode B uses local MCP + Windows Agent."
Write-Host "  Next: restart Cursor, enable 'desktopuseagent' MCP, open Control Center."
