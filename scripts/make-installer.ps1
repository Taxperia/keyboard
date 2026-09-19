$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "artifacts\publish\KeyBridge-win-x64\KeyBridge.exe"
$iss = Join-Path $root "installer\KeyBridge.iss"

if (-not (Test-Path $exe)) {
    & (Join-Path $PSScriptRoot "publish-release.ps1") -Mode self-contained
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$iscc = Get-Command "iscc" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
}

if (-not $iscc) {
    throw "Inno Setup Compiler bulunamadi. Inno Setup kurulduktan sonra bu script KeyBridgeSetup.exe uretebilir."
}

& $iscc.Source $iss
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
