$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$layout = Join-Path $root "artifacts\layout"
if (-not (Test-Path $layout)) {
  & (Join-Path $PSScriptRoot "pack.ps1")
}
$target = Join-Path $env:LOCALAPPDATA "SemanticDesktop\current"
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item (Join-Path $layout "*") $target -Recurse
$manifest = @{
  schemaVersion = 1
  version = "1.12.0"
  channel = "stable"
  minCompatibleApi = "1.0.0"
  files = @()
}
Get-ChildItem $target -Recurse -File | ForEach-Object {
  $rel = $_.FullName.Substring($target.Length).TrimStart("\").Replace("\", "/")
  $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  $manifest.files += @{ path = $rel; sha256 = $hash }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $target "install-manifest.json") -Encoding utf8
Write-Host "Installed to $target"
