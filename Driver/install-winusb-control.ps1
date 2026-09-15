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

Write-Host "Registering WinUSB control package..."
& pnputil.exe /add-driver $inf /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil /add-driver failed with exit code $LASTEXITCODE. Check catalog signing and Windows driver policy."
}

Write-Host "Forcing the package onto MI_00..."
$devcon = $null
$candidates = @(
    (Get-Command devcon.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Tools\x64\devcon.exe'),
    (Join-Path ${env:ProgramFiles} 'Windows Kits\10\Tools\x64\devcon.exe')
) | Where-Object { $_ -and (Test-Path $_) }
if ($candidates) { $devcon = $candidates | Select-Object -First 1 }

if (-not $devcon) {
    throw "DevCon.exe was not found. Install the Windows SDK/WDK or place devcon.exe on PATH."
}

& $devcon update $inf $hardwareId
if ($LASTEXITCODE -ne 0) {
    throw "DevCon update failed with exit code $LASTEXITCODE for $hardwareId."
}

Write-Host "Restarting MI_00..."
& pnputil.exe /restart-device $hardwareId
if ($LASTEXITCODE -ne 0) {
    Write-Warning "pnputil could not restart by hardware ID; unplug/replug the Apple device and continue."
}

Write-Host "`nFinal driver state:`n"
& pnputil.exe /enum-devices /deviceid $hardwareId /drivers
