$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$agent = Join-Path $root "services\windows-agent\src\SemanticDesktop.Agent\SemanticDesktop.Agent.csproj"
$cli = Join-Path $root "services\windows-agent\src\SemanticDesktop.Cli\SemanticDesktop.Cli.csproj"
Start-Process -FilePath $dotnet -ArgumentList @("run","--project",$agent,"--no-build") -WindowStyle Hidden
Start-Sleep -Seconds 2
& $dotnet run --project $cli --no-build -- @args
