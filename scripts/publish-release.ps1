param(
    [ValidateSet("framework-dependent", "self-contained")]
    [string]$Mode = "self-contained"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "KeyBridge\KeyBridge.csproj"
$output = Join-Path $root "artifacts\publish\KeyBridge-win-x64"
$selfContained = if ($Mode -eq "self-contained") { "true" } else { "false" }

New-Item -ItemType Directory -Force -Path $output | Out-Null

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained:$selfContained",
    "-o", $output,
    "/p:RestoreIgnoreFailedSources=true",
    "/p:PublishSingleFile=true"
)

if ($Mode -eq "self-contained") {
    $publishArgs += "/p:IncludeNativeLibrariesForSelfExtract=true"
    $publishArgs += "/p:EnableCompressionInSingleFile=true"
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination (Join-Path $output "LICENSE") -Force
Copy-Item -LiteralPath (Join-Path $root "NOTICE") -Destination (Join-Path $output "NOTICE") -Force
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $output "README.md") -Force
Copy-Item -LiteralPath (Join-Path $root "scripts\setup-private-network.ps1") -Destination (Join-Path $output "setup-private-network.ps1") -Force

Write-Host ""
Write-Host "KeyBridge publish output:"
Write-Host $output
