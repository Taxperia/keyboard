param(
    [string]$ExePath = (Join-Path $PSScriptRoot "KeyBridge.exe")
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Bu betiği yönetici olarak çalıştırın."
}

$resolvedExe = (Resolve-Path -LiteralPath $ExePath -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedExe) -ne "KeyBridge.exe") {
    throw "ExePath, KeyBridge.exe dosyasını göstermelidir."
}

$rules = @(
    @{ Name = "KeyBridge.PrivateLAN.TCP"; Protocol = "TCP"; LocalPort = @(48741, 48742, 48743, 48744, 48745) },
    @{ Name = "KeyBridge.PrivateLAN.UDP"; Protocol = "UDP"; LocalPort = @(48740, 48741, 48742) }
)

foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
    if ($existing) {
        Remove-NetFirewallRule -Name $rule.Name
    }

    New-NetFirewallRule `
        -Name $rule.Name `
        -DisplayName ($rule.Name + " (yalnızca Özel yerel ağ)") `
        -Direction Inbound `
        -Action Allow `
        -Enabled True `
        -Profile Private `
        -Program $resolvedExe `
        -Protocol $rule.Protocol `
        -LocalPort $rule.LocalPort `
        -RemoteAddress LocalSubnet | Out-Null
}

Write-Output "KeyBridge için Özel ağ / yerel alt ağ kuralları hazır: $resolvedExe"
