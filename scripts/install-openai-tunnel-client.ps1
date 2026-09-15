param(
  [string]$DestinationRoot
)

$ErrorActionPreference = "Stop"

function Get-WindowsClientArch {
  if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") {
    return "arm64"
  }
  return "amd64"
}

function Resolve-ToolsRoot {
  param([string]$OverrideRoot)
  if ($OverrideRoot) {
    return $OverrideRoot
  }
  return Join-Path $env:LOCALAPPDATA "DesktopUseAgent\tools\tunnel-client"
}

function Get-CanonicalRoot {
  param([string]$Destination)

  $full = [System.IO.Path]::GetFullPath($Destination)
  if (-not $full.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
    $full += [System.IO.Path]::DirectorySeparatorChar
  }
  return $full
}

function Test-ResolvedUnderRoot {
  param(
    [string]$TargetPath,
    [string]$CanonicalRoot
  )

  $resolved = [System.IO.Path]::GetFullPath($TargetPath)
  if ($resolved.StartsWith($CanonicalRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    return $true
  }
  $rootWithoutSep = $CanonicalRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
  )
  return $resolved.Equals($rootWithoutSep, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-ZipEntryNameSafe {
  param([string]$EntryName)

  if ([string]::IsNullOrWhiteSpace($EntryName)) {
    return $false
  }
  if ($EntryName.IndexOf([char]0) -ge 0) {
    return $false
  }
  if ([System.IO.Path]::IsPathRooted($EntryName)) {
    return $false
  }

  $normalized = $EntryName.Replace("\", "/")
  # Reject any colon to block NTFS alternate data stream paths (e.g. file:stream).
  if ($normalized -match ':') {
    return $false
  }
  if ($normalized.StartsWith("//")) {
    return $false
  }
  if ($EntryName.StartsWith("\\")) {
    return $false
  }
  if ($normalized -match '(^|/)\.\.(/|$)') {
    return $false
  }

  return $true
}

function Expand-ZipSafely {
  param(
    [string]$ZipPath,
    [string]$Destination
  )

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  if (Test-Path $Destination) {
    Remove-Item $Destination -Recurse -Force
  }
  New-Item -ItemType Directory -Path $Destination -Force | Out-Null
  $canonicalRoot = Get-CanonicalRoot -Destination $Destination

  $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
  try {
    foreach ($entry in $zip.Entries) {
      if ([string]::IsNullOrWhiteSpace($entry.FullName)) {
        continue
      }
      if (-not (Test-ZipEntryNameSafe -EntryName $entry.FullName)) {
        throw "Unsafe archive entry rejected: $($entry.FullName)"
      }

      $relativePath = $entry.FullName.Replace("/", [System.IO.Path]::DirectorySeparatorChar)
      $targetPath = Join-Path $Destination $relativePath
      if (-not (Test-ResolvedUnderRoot -TargetPath $targetPath -CanonicalRoot $canonicalRoot)) {
        throw "Archive traversal detected for entry: $($entry.FullName)"
      }

      if ($entry.FullName.EndsWith("/") -or $entry.FullName.EndsWith("\")) {
        New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
        continue
      }

      $targetDir = Split-Path $targetPath -Parent
      if ($targetDir -and -not (Test-ResolvedUnderRoot -TargetPath $targetDir -CanonicalRoot $canonicalRoot)) {
        throw "Archive traversal detected for parent path: $targetDir"
      }
      if ($targetDir -and -not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
      }

      $resolvedTarget = [System.IO.Path]::GetFullPath($targetPath)
      if (-not (Test-ResolvedUnderRoot -TargetPath $resolvedTarget -CanonicalRoot $canonicalRoot)) {
        throw "Archive traversal detected for resolved path: $resolvedTarget"
      }

      [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $resolvedTarget, $true)
    }
  }
  finally {
    $zip.Dispose()
  }
}

function Set-StableCurrentLink {
  param(
    [string]$ToolsRoot,
    [string]$VersionDir
  )

  $currentLink = Join-Path $ToolsRoot "current"
  if (Test-Path -LiteralPath $currentLink) {
    $item = Get-Item -LiteralPath $currentLink -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
      if ($item.PSIsContainer) {
        [System.IO.Directory]::Delete($currentLink)
      }
      else {
        [System.IO.File]::Delete($currentLink)
      }
    }
    elseif ($item.PSIsContainer) {
      Remove-Item -LiteralPath $currentLink -Recurse -Force
    }
    else {
      Remove-Item -LiteralPath $currentLink -Force
    }
  }
  New-Item -ItemType Junction -Path $currentLink -Target $VersionDir | Out-Null
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$arch = Get-WindowsClientArch
$toolsRoot = Resolve-ToolsRoot -OverrideRoot $DestinationRoot
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/openai/tunnel-client/releases/latest" -Headers @{ "User-Agent" = "DesktopUseAgent-installer" }
if (-not $release.tag_name) {
  throw "Malformed release metadata: missing tag_name."
}

$tag = [string]$release.tag_name
if ($tag -notmatch '^v[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') {
  throw "Malformed release metadata: invalid tag_name '$tag'."
}
$assetPattern = "^tunnel-client-v.+?-windows-$arch\.zip$"
$asset = $release.assets | Where-Object {
  $_.name -match $assetPattern -and $_.name -notmatch "runtime"
} | Select-Object -First 1

if (-not $asset) {
  throw "No full-client Windows $arch zip asset found for release $tag."
}
if (-not $asset.browser_download_url) {
  throw "Malformed release asset metadata: missing browser_download_url for $($asset.name)."
}
if (-not $asset.digest -or $asset.digest -notmatch '^sha256:([a-fA-F0-9]{64})$') {
  throw "Malformed release asset digest for $($asset.name): expected sha256:<64 hex chars>."
}

$expectedHash = $Matches[1].ToLowerInvariant()
$versionDir = Join-Path $toolsRoot $tag
$stableExe = Join-Path (Join-Path $toolsRoot "current") "tunnel-client.exe"

if (Test-Path $versionDir) {
  $existingExe = Get-ChildItem $versionDir -Recurse -Filter "tunnel-client.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($existingExe) {
    Set-StableCurrentLink -ToolsRoot $toolsRoot -VersionDir $versionDir
    $versionOutput = & $existingExe.FullName --version 2>&1
    Write-Host "tunnel-client already installed at $($existingExe.FullName)"
    Write-Host "Version: $versionOutput"
    Write-Host "Stable path: $stableExe"
    exit 0
  }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("DesktopUseAgent-tunnel-client-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$zipPath = Join-Path $tempRoot $asset.name

try {
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
  $actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actualHash -ne $expectedHash) {
    throw "SHA-256 mismatch for $($asset.name). Expected $expectedHash but got $actualHash."
  }

  Expand-ZipSafely -ZipPath $zipPath -Destination $versionDir

  $exe = Get-ChildItem $versionDir -Recurse -Filter "tunnel-client.exe" | Select-Object -First 1
  if (-not $exe) {
    throw "tunnel-client.exe not found after extracting $($asset.name)."
  }

  Set-StableCurrentLink -ToolsRoot $toolsRoot -VersionDir $versionDir
  $versionOutput = & $exe.FullName --version 2>&1
  Write-Host "Installed tunnel-client $tag for windows-$arch"
  Write-Host "Executable: $($exe.FullName)"
  Write-Host "Stable path: $stableExe"
  Write-Host "Version: $versionOutput"
}
finally {
  if (Test-Path $tempRoot) {
    Remove-Item $tempRoot -Recurse -Force
  }
}
