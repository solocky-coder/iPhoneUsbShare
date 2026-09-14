# Run elevated on the target Windows 10 x64 machine.
# This script stages AppleNcm and then explicitly updates the MI_02 hardware ID.
# Staging alone is intentionally not treated as proof of binding.
param(
    [string]$HardwareId = 'USB\VID_05AC&PID_12AB&MI_02',
    [string]$DeviceInstanceId = '',
    [string]$DriverPackage = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$inf = if ($DriverPackage) { $DriverPackage } else { Join-Path $root 'artifacts\AppleNcm\AppleNcm.inf' }
if (-not (Test-Path $inf)) { $inf = Join-Path $root 'AppleNcm.inf' }
if (-not (Test-Path $inf)) { throw "AppleNcm.inf not found." }

$packageDir = Split-Path -Parent $inf
$sys = Join-Path $packageDir 'AppleNcm.sys'
$cat = Join-Path $packageDir 'AppleNcm.cat'
if (-not (Test-Path $sys)) { throw "AppleNcm.sys not found next to $inf" }
if (-not (Test-Path $cat)) { throw "AppleNcm.cat not found next to $inf" }

Write-Host "AppleNcm bring-up INF: $inf"
Write-Host "AppleNcm target hardware ID: $HardwareId"
if ($DeviceInstanceId) { Write-Host "AppleNcm target instance: $DeviceInstanceId" }

# First stage/register the package. This is necessary but is NOT the forced
# binding experiment by itself.
Write-Host "Registering AppleNcm package..."
& pnputil.exe /add-driver $inf /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil /add-driver failed with exit code $LASTEXITCODE. Check driver signing/package validity before changing USB/NCM code."
}

# DevCon's update operation takes a hardware ID, not a device-instance ID.
# Windows then applies the supplied INF to matching devices. This is the
# explicit bring-up step intended to replace the currently selected Apple
# Netaapl driver for MI_02.
$devcon = $null
$candidates = @(
    (Get-Command devcon.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Tools\x64\devcon.exe'),
    (Join-Path ${env:ProgramFiles} 'Windows Kits\10\Tools\x64\devcon.exe')
) | Where-Object { $_ -and (Test-Path $_) }
if ($candidates) { $devcon = $candidates | Select-Object -First 1 }

if (-not $devcon) {
    throw @"
DevCon.exe was not found. The package is staged, but the forced binding experiment was NOT performed.
Install the Windows SDK/WDK containing DevCon, put devcon.exe on PATH, and rerun this script.
Do not interpret a successful pnputil /add-driver result as proof that AppleNcm replaced Netaapl.
"@
}

Write-Host "Forcing driver update with: $devcon"
& $devcon update $inf $HardwareId
if ($LASTEXITCODE -ne 0) {
    throw "DevCon update failed with exit code $LASTEXITCODE for hardware ID $HardwareId."
}

Write-Host "`nDriver state after forced update:`n"
if ($DeviceInstanceId) {
    & pnputil.exe /enum-devices /instanceid $DeviceInstanceId /drivers
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "pnputil could not enumerate $DeviceInstanceId. Check Device Manager/SetupAPI logs manually."
    }
} else {
    Write-Host "No instance ID supplied; enumerating devices matching $HardwareId."
    & pnputil.exe /enum-devices /deviceid $HardwareId /drivers
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "pnputil could not enumerate devices matching $HardwareId. Check Device Manager/SetupAPI logs manually."
    }
}

Write-Host "`nExpected decisive state: Service=AppleNcm, selected driver AppleNcm.inf, and no Code 10."
Write-Host "If AppleNcm.sys executes, next collect DriverEntry/EvtDeviceAdd/EvtDevicePrepareHardware logs before changing NCM behavior."
