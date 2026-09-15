$ErrorActionPreference = "Stop"
$removed = $false
$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\DesktopUseAgent.lnk"
if (Test-Path $shortcut) {
  Remove-Item $shortcut -Force
  Write-Host "Removed $shortcut"
  $removed = $true
}
foreach ($name in @("DesktopUseAgent", "SemanticDesktop")) {
  $target = Join-Path $env:LOCALAPPDATA $name
  if (Test-Path $target) {
    Remove-Item $target -Recurse -Force
    Write-Host "Removed $target"
    $removed = $true
  }
}
if (-not $removed) {
  Write-Host "Nothing to uninstall."
}
