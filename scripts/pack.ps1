$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
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
  if (Test-Path (Join-Path $mcp "node_modules")) {
    & npm --prefix $mcp run build
    if ($LASTEXITCODE -ne 0) { throw "MCP gateway build failed with exit code $LASTEXITCODE." }
  }
  if (-not (Test-Path (Join-Path $mcp "dist\index.js"))) {
    throw "MCP gateway is not built. Run npm install and npm run build in apps\mcp-server."
  }
  New-Item -ItemType Directory -Path (Join-Path $out "mcp") | Out-Null
  Get-ChildItem $mcp -Force |
    Where-Object { $_.Name -ne "node_modules" } |
    Copy-Item -Destination (Join-Path $out "mcp") -Recurse -Force
  & npm --prefix (Join-Path $out "mcp") install --omit=dev --ignore-scripts
  if ($LASTEXITCODE -ne 0) { throw "MCP production dependency install failed with exit code $LASTEXITCODE." }
}
Write-Host "Layout written to $out"
