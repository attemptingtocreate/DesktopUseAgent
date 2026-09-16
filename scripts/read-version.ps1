$ErrorActionPreference = "Stop"
$root = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { Get-Location }
$versionFile = Join-Path $root "VERSION"
if (-not (Test-Path $versionFile)) {
  throw "VERSION file not found at $versionFile"
}
$version = (Get-Content $versionFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
  throw "VERSION file is empty."
}
return $version
