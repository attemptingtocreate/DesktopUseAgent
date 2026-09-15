$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$out = Join-Path $root "artifacts\layout"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null
& $dotnet publish (Join-Path $root "services\windows-agent\src\SemanticDesktop.Agent\SemanticDesktop.Agent.csproj") -c Release -o (Join-Path $out "agent") --self-contained false
$cc = Join-Path $root "apps\desktop\SemanticDesktop.ControlCenter\SemanticDesktop.ControlCenter.csproj"
if (Test-Path $cc) {
  & $dotnet publish $cc -c Release -p:Platform=x64 -o (Join-Path $out "control-center")
}
if (Test-Path (Join-Path $root "apps\mcp-server\package.json")) {
  New-Item -ItemType Directory -Path (Join-Path $out "mcp") | Out-Null
  Copy-Item (Join-Path $root "apps\mcp-server\*") (Join-Path $out "mcp") -Recurse -Exclude node_modules,dist
}
Write-Host "Layout written to $out"
