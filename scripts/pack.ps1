$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = & (Join-Path $PSScriptRoot "read-version.ps1")
if ($version -ne (Get-Content (Join-Path $root "services\windows-agent\src\SemanticDesktop.Core\Production\RuntimeCompat.cs") -Raw | Select-String 'ProductVersion = "([^"]+)"').Matches.Groups[1].Value) {
  Write-Warning "RuntimeCompat.ProductVersion may be out of sync with VERSION ($version)."
}
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$out = Join-Path $root "artifacts\layout"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null
& $dotnet publish (Join-Path $root "services\windows-agent\src\SemanticDesktop.Agent\SemanticDesktop.Agent.csproj") -c Release -r win-x64 -o (Join-Path $out "agent") --self-contained true
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed with exit code $LASTEXITCODE." }
$cc = Join-Path $root "apps\desktop\SemanticDesktop.ControlCenter\SemanticDesktop.ControlCenter.csproj"
if (Test-Path $cc) {
  & $dotnet publish $cc -c Release -r win-x64 --self-contained true -p:Platform=x64 -o (Join-Path $out "control-center")
  if ($LASTEXITCODE -ne 0) { throw "Control Center publish failed with exit code $LASTEXITCODE." }
}
$mcp = Join-Path $root "apps\mcp-server"
if (Test-Path (Join-Path $mcp "package.json")) {
  if (-not (Test-Path (Join-Path $mcp "node_modules"))) {
    & npm --prefix $mcp ci
    if ($LASTEXITCODE -ne 0) { throw "MCP npm ci failed with exit code $LASTEXITCODE." }
  }
  & npm --prefix $mcp run build
  if ($LASTEXITCODE -ne 0) { throw "MCP gateway build failed with exit code $LASTEXITCODE." }
  if (-not (Test-Path (Join-Path $mcp "dist\index.js"))) {
    throw "MCP gateway is not built. Run npm install and npm run build in apps\mcp-server."
  }
  $pkg = Get-Content (Join-Path $mcp "package.json") -Raw | ConvertFrom-Json
  if ($pkg.version -ne $version) {
    Write-Warning "apps/mcp-server package.json version ($($pkg.version)) differs from VERSION ($version)."
  }
  New-Item -ItemType Directory -Path (Join-Path $out "mcp") | Out-Null
  Get-ChildItem $mcp -Force |
    Where-Object { $_.Name -ne "node_modules" } |
    Copy-Item -Destination (Join-Path $out "mcp") -Recurse -Force
  & npm --prefix (Join-Path $out "mcp") install --omit=dev --ignore-scripts
  if ($LASTEXITCODE -ne 0) { throw "MCP production dependency install failed with exit code $LASTEXITCODE." }
}
$buildRobloxPlugin = Join-Path $root "scripts\build-roblox-plugin.ps1"
if (Test-Path $buildRobloxPlugin) {
  & $buildRobloxPlugin
  $pluginArtifact = Join-Path $root "artifacts\roblox-studio\DesktopUseAgent.rbxmx"
  if (Test-Path $pluginArtifact) {
    $pluginOut = Join-Path $out "plugins\roblox-studio"
    New-Item -ItemType Directory -Path $pluginOut -Force | Out-Null
    Copy-Item $pluginArtifact (Join-Path $pluginOut "DesktopUseAgent.rbxmx") -Force
    Copy-Item (Join-Path $root "plugins\roblox-studio\README.md") (Join-Path $pluginOut "README.md") -Force
  }
}
$buildBlenderAddon = Join-Path $root "scripts\build-blender-addon.ps1"
if (Test-Path $buildBlenderAddon) {
  & $buildBlenderAddon
  $addonArtifact = Join-Path $root "artifacts\blender\desktopuseagent_blender.zip"
  if (Test-Path $addonArtifact) {
    $addonOut = Join-Path $out "plugins\blender"
    New-Item -ItemType Directory -Path $addonOut -Force | Out-Null
    Copy-Item $addonArtifact (Join-Path $addonOut "desktopuseagent_blender.zip") -Force
    Copy-Item (Join-Path $root "plugins\blender\README.md") (Join-Path $addonOut "README.md") -Force
  }
}
Copy-Item (Join-Path $root "VERSION") (Join-Path $out "VERSION") -Force

# Cursor Mode B support (zip-only users can configure without the git repo)
$scriptsLayout = Join-Path $out "scripts"
New-Item -ItemType Directory -Path $scriptsLayout -Force | Out-Null
$configureSrc = Join-Path $PSScriptRoot "configure-cursor.ps1"
if (Test-Path $configureSrc) {
  Copy-Item $configureSrc (Join-Path $scriptsLayout "configure-cursor.ps1") -Force
}
$skillSrc = Join-Path $root ".cursor\skills\desktopuseagent\SKILL.md"
if (Test-Path $skillSrc) {
  $skillOut = Join-Path $out "mcp\cursor-skill"
  New-Item -ItemType Directory -Path $skillOut -Force | Out-Null
  Copy-Item $skillSrc (Join-Path $skillOut "SKILL.md") -Force
}
$cursorExample = Join-Path $root ".cursor\mcp.json.example"
if (Test-Path $cursorExample) {
  $cursorOut = Join-Path $out ".cursor"
  New-Item -ItemType Directory -Path $cursorOut -Force | Out-Null
  Copy-Item $cursorExample (Join-Path $cursorOut "mcp.json.example") -Force
}

Write-Host "Layout written to $out (product version $version, win-x64)"
