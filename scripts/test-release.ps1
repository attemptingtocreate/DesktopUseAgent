$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path $env:TEMP ("dua-release-test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
  & (Join-Path $PSScriptRoot "release.ps1") -SkipTests
  if ($LASTEXITCODE -ne 0) { throw "release.ps1 failed." }
  $version = & (Join-Path $PSScriptRoot "read-version.ps1")
  $zip = Join-Path $root "artifacts\release\DesktopUseAgent-$version-win-x64.zip"
  if (-not (Test-Path $zip)) { throw "Release zip missing: $zip" }
  Expand-Archive -Path $zip -DestinationPath $temp -Force
  $installScript = Join-Path $temp "install.ps1"
  if (-not (Test-Path $installScript)) { throw "install.ps1 missing from release zip." }
  $layout = Join-Path $temp "layout"
  if (-not (Test-Path (Join-Path $layout "agent"))) { throw "layout/agent missing from release zip." }
  $env:LOCALAPPDATA = Join-Path $temp "local"
  New-Item -ItemType Directory -Path $env:LOCALAPPDATA -Force | Out-Null
  & $installScript
  $installed = Join-Path $env:LOCALAPPDATA "DesktopUseAgent\current\install-manifest.json"
  if (-not (Test-Path $installed)) { throw "install-manifest.json not created by install.ps1" }
  Write-Host "Release packaging smoke test passed."
} finally {
  Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
