param(
    [switch]$DryRun,
    [switch]$Live,
    [int]$Iterations = 1,
    [int]$Monitor = 0,
    [string]$Url = "about:blank",
    [string]$OutputPath = "",
    [string]$Pipe = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$cliProject = Join-Path $root "services\windows-agent\src\SemanticDesktop.Cli\SemanticDesktop.Cli.csproj"
$argsList = @("run", "--project", $cliProject, "-c", "Debug", "--")
if ($Pipe) { $argsList += "--pipe=$Pipe" }
$argsList += "benchmark"
if ($DryRun) { $argsList += "--dry-run" }
if ($Live) { $argsList += "--live" }
$argsList += @("--iterations", "$Iterations", "--monitor", "$Monitor", "--url", $Url)

& $dotnet build $cliProject -c Debug | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$output = & $dotnet @argsList 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Output $output
    exit $LASTEXITCODE
}

if ($OutputPath) {
    Set-Content -Path $OutputPath -Value $output -Encoding UTF8
}

Write-Output $output
