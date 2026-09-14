# Run elevated on the target Windows 10 x64 machine.
# This script registers the AppleNcm package and targets only the observed
# Apple NCM function. It does not uninstall Apple's Netaapl package globally.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$inf = Join-Path $root 'artifacts\AppleNcm\AppleNcm.inf'
if (-not (Test-Path $inf)) { $inf = Join-Path $root 'AppleNcm.inf' }
if (-not (Test-Path $inf)) { throw "AppleNcm.inf not found." }
Write-Host "Registering $inf"
& pnputil.exe /add-driver $inf /install
Write-Host "`nCurrent Apple USB NCM candidates:`n"
& pnputil.exe /enum-devices /class Net /connected
Write-Host "`nIf the AppleNcm package is test-signed, Windows must accept its signing policy before this step."
