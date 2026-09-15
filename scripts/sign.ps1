$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$layout = Join-Path $root "artifacts\layout"
if (-not (Test-Path $layout)) {
  throw "Run scripts/pack.ps1 first."
}
$signtool = Get-Command signtool -ErrorAction SilentlyContinue
if (-not $signtool) {
  Write-Host "signtool not found; binaries remain unsigned. Set a code-signing cert in CI to enable Authenticode."
  exit 0
}
if (-not $env:SIGN_THUMBPRINT -and -not $env:SIGN_CERT_PATH) {
  Write-Host "SIGN_THUMBPRINT or SIGN_CERT_PATH not set; skipping Authenticode."
  exit 0
}
Get-ChildItem $layout -Recurse -Include *.exe,*.dll | ForEach-Object {
  if ($env:SIGN_THUMBPRINT) {
    & signtool sign /sha1 $env:SIGN_THUMBPRINT /td sha256 /fd sha256 /tr http://timestamp.digicert.com $_.FullName
  } else {
    & signtool sign /f $env:SIGN_CERT_PATH /p $env:SIGN_CERT_PASSWORD /td sha256 /fd sha256 /tr http://timestamp.digicert.com $_.FullName
  }
}
