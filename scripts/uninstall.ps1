$ErrorActionPreference = "Stop"
$target = Join-Path $env:LOCALAPPDATA "SemanticDesktop"
if (Test-Path $target) {
  Remove-Item $target -Recurse -Force
  Write-Host "Removed $target"
} else {
  Write-Host "Nothing to uninstall."
}
