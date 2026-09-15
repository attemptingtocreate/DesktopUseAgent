$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$layout = Join-Path $root "artifacts\layout"
if (-not (Test-Path $layout)) {
  & (Join-Path $PSScriptRoot "pack.ps1")
}
$target = Join-Path $env:LOCALAPPDATA "DesktopUseAgent\current"
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item (Join-Path $layout "*") $target -Recurse
$appExe = Join-Path $target "control-center\DesktopUseAgent.exe"
if (-not (Test-Path $appExe)) {
  throw "DesktopUseAgent.exe is missing from the packaged control-center layout."
}
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
$programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcut = Join-Path $programs "DesktopUseAgent.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $appExe
$link.WorkingDirectory = Split-Path $appExe -Parent
$link.IconLocation = "$appExe,0"
$link.Description = "DesktopUseAgent"
$link.Save()
Write-Host "Installed to $target"
Write-Host "Start Menu shortcut created at $shortcut"
