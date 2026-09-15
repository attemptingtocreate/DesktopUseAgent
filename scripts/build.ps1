$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
& $dotnet build (Join-Path $root "services\windows-agent\SemanticDesktop.Agent.sln") -c Debug
& $dotnet build (Join-Path $root "fixtures\uia-test-app\UiaTestApp.csproj") -c Debug
& $dotnet build (Join-Path $root "apps\desktop\SemanticDesktop.ControlCenter\SemanticDesktop.ControlCenter.csproj") -c Debug -p:Platform=x64
