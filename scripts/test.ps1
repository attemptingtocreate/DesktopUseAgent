$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
& $dotnet test (Join-Path $root "services\windows-agent\SemanticDesktop.Agent.sln") -c Debug --no-build
if ($LASTEXITCODE -ne 0) {
  & $dotnet test (Join-Path $root "services\windows-agent\SemanticDesktop.Agent.sln") -c Debug
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (Test-Path (Join-Path $root "apps\mcp-server\package.json")) {
  Push-Location (Join-Path $root "apps\mcp-server")
  try {
    if (Test-Path "node_modules") {
      npm test --silent
      if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
  } finally {
    Pop-Location
  }
}
