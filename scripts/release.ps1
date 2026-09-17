param(
  [switch]$SkipTests,
  [switch]$Sign
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = & (Join-Path $PSScriptRoot "read-version.ps1")
$releaseDir = Join-Path $root "artifacts\release"
$staging = Join-Path $releaseDir "staging-$version"
$zipName = "DesktopUseAgent-$version-win-x64.zip"
$zipPath = Join-Path $releaseDir $zipName
$checksumPath = "$zipPath.sha256"

if (-not $SkipTests) {
  & (Join-Path $PSScriptRoot "build.ps1")
  if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed." }
  & (Join-Path $PSScriptRoot "test.ps1")
  if ($LASTEXITCODE -ne 0) { throw "test.ps1 failed." }
  Push-Location (Join-Path $root "apps\mcp-server")
  try {
    if (-not (Test-Path "node_modules")) { npm ci }
    npm test
    if ($LASTEXITCODE -ne 0) { throw "MCP tests failed." }
  } finally {
    Pop-Location
  }
}

& (Join-Path $PSScriptRoot "pack.ps1")
if ($Sign) {
  & (Join-Path $PSScriptRoot "sign.ps1")
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

Copy-Item (Join-Path $root "artifacts\layout") (Join-Path $staging "layout") -Recurse
Copy-Item (Join-Path $PSScriptRoot "install.ps1") $staging
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $staging
Copy-Item (Join-Path $PSScriptRoot "configure-cursor.ps1") $staging
Copy-Item (Join-Path $root "VERSION") $staging
foreach ($doc in @("README.md", "LICENSE", "NOTICE", "SECURITY.md", "CONTRIBUTING.md", "CHANGELOG.md", "CODE_OF_CONDUCT.md", "AGENTS.md")) {
  $src = Join-Path $root $doc
  if (Test-Path $src) { Copy-Item $src $staging }
}
$docsOut = Join-Path $staging "docs"
New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
Get-ChildItem (Join-Path $root "docs") -File | Copy-Item -Destination $docsOut
$scriptsOut = Join-Path $staging "scripts"
New-Item -ItemType Directory -Path $scriptsOut -Force | Out-Null
foreach ($script in @(
  "configure-cursor.ps1",
  "install-roblox-plugin.ps1",
  "install-blender-addon.ps1",
  "build-roblox-plugin.ps1",
  "build-blender-addon.ps1",
  "setup-chatgpt-tunnel.ps1",
  "start-chatgpt-tunnel.ps1",
  "install-openai-tunnel-client.ps1",
  "benchmark.ps1"
)) {
  $src = Join-Path $PSScriptRoot $script
  if (Test-Path $src) { Copy-Item $src $scriptsOut }
}
$skillSrc = Join-Path $root ".cursor\skills\desktopuseagent\SKILL.md"
if (Test-Path $skillSrc) {
  $skillStaging = Join-Path $staging ".cursor\skills\desktopuseagent"
  New-Item -ItemType Directory -Path $skillStaging -Force | Out-Null
  Copy-Item $skillSrc (Join-Path $skillStaging "SKILL.md") -Force
  $skillScripts = Join-Path $scriptsOut "cursor-skill"
  New-Item -ItemType Directory -Path $skillScripts -Force | Out-Null
  Copy-Item $skillSrc (Join-Path $skillScripts "SKILL.md") -Force
}
$cursorExample = Join-Path $root ".cursor\mcp.json.example"
if (Test-Path $cursorExample) {
  $cursorOut = Join-Path $staging ".cursor"
  New-Item -ItemType Directory -Path $cursorOut -Force | Out-Null
  Copy-Item $cursorExample (Join-Path $cursorOut "mcp.json.example")
}

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath -Force
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksumPath -Value "$hash  $zipName" -Encoding ascii
Write-Host "Release artifact: $zipPath"
Write-Host "SHA256: $hash"
