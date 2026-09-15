# Run elevated on the target Windows 10 x64 machine.
# Installs the iPhoneUsbShare WinUSB control driver on Apple MI_00.
# The package must contain a valid WinUsbControl.cat signed for the target
# Windows driver-signing policy.

param(
    [string]$DriverPackage = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageDir = if ($DriverPackage) { $DriverPackage } else { $root }
$inf = Join-Path $packageDir 'WinUsbControl.inf'
$cat = Join-Path $packageDir 'WinUsbControl.cat'

if (-not (Test-Path $inf)) { throw "WinUsbControl.inf not found: $inf" }
if (-not (Test-Path $cat)) {
    throw "WinUsbControl.cat not found. The WinUSB package must be signed before normal Secure Boot installation."
}

$hardwareId = 'USB\VID_05AC&PID_12AB&MI_00'
Write-Host "WinUSB control package: $inf"
Write-Host "Target hardware ID: $hardwareId"

Write-Host "Registering and installing WinUSB control package..."
& pnputil.exe /add-driver $inf /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil /add-driver failed with exit code $LASTEXITCODE. Check catalog signing and Windows driver policy."
}

Write-Host "WinUSB control driver installation completed."
Write-Host "The package was installed by PnPUtil on matching MI_00 device instances."
Write-Host "Unplug and reconnect the Apple device before starting iPhoneUsbShare."

Write-Host "`nFinal driver state:`n"
& pnputil.exe /enum-devices /deviceid $hardwareId /drivers
