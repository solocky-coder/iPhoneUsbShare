# Run elevated on the target Windows 10 x64 machine.
# Installs the Usbccgp non-default configuration override on the Apple
# composite parent, then installs WinUSB on Apple MI_00.
# The package must contain signed catalogs for the target Windows
# driver-signing policy.

param(
    [string]$DriverPackage = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageDir = if ($DriverPackage) { $DriverPackage } else { $root }
$parentInf = Join-Path $packageDir 'AppleUsbCompositeConfiguration.inf'
$parentCat = Join-Path $packageDir 'AppleUsbCompositeConfiguration.cat'
$inf = Join-Path $packageDir 'WinUsbControl.inf'
$cat = Join-Path $packageDir 'WinUsbControl.cat'

if (-not (Test-Path $parentInf)) { throw "AppleUsbCompositeConfiguration.inf not found: $parentInf" }
if (-not (Test-Path $parentCat)) {
    throw "AppleUsbCompositeConfiguration.cat not found: $parentCat"
}
if (-not (Test-Path $inf)) { throw "WinUsbControl.inf not found: $inf" }
if (-not (Test-Path $cat)) {
    throw "WinUsbControl.cat not found: $cat"
}

$parentHardwareId = 'USB\VID_05AC&PID_12AB'
$hardwareId = 'USB\VID_05AC&PID_12AB&MI_00'
Write-Host "Usbccgp configuration package: $parentInf"
Write-Host "Target parent hardware ID: $parentHardwareId"
Write-Host "Target WinUSB hardware ID: $hardwareId"

if (-not ('IPhoneUsbShare_NewDev' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class IPhoneUsbShare_NewDev
{
    private const uint INSTALLFLAG_FORCE = 0x00000001;

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        out bool rebootRequired);

    public static bool InstallForced(string hardwareId, string infPath, out bool rebootRequired)
    {
        bool result = UpdateDriverForPlugAndPlayDevicesW(
            IntPtr.Zero,
            hardwareId,
            infPath,
            INSTALLFLAG_FORCE,
            out rebootRequired);

        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error,
                "UpdateDriverForPlugAndPlayDevicesW(INSTALLFLAG_FORCE) failed");
        }

        return result;
    }
}
'@
}

Write-Host "`n=== Step 1: Configure Usbccgp to select USB configuration 5 ==="
Write-Host "Staging composite-parent configuration package..."
& pnputil.exe /add-driver $parentInf /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil /add-driver failed for AppleUsbCompositeConfiguration.inf with exit code $LASTEXITCODE. Check catalog signing and Windows driver policy."
}

Write-Host "Forcing the composite-parent configuration package onto the Apple parent..."
$rebootRequired = $false
[IPhoneUsbShare_NewDev]::InstallForced($parentHardwareId, $parentInf, [ref]$rebootRequired) | Out-Null
if ($rebootRequired) {
    Write-Warning "Windows reports that a reboot may be required after the composite-parent driver update."
}

$parent = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'USB\VID_05AC&PID_12AB\*' } |
    Select-Object -First 1

if (-not $parent) {
    throw "Apple USB composite parent was not found after installing the Usbccgp configuration package."
}

Write-Host "Apple parent instance: $($parent.InstanceId)"

$parentHardwareKey = Join-Path $env:SystemRoot "System32\config\SYSTEM"
$registryPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($parent.InstanceId)"
try {
    $override = (Get-ItemProperty -Path $registryPath -Name OriginalConfigurationValue -ErrorAction Stop).OriginalConfigurationValue
    Write-Host "OriginalConfigurationValue on parent hardware key: $override"
    if ([int]$override -ne 5) {
        throw "Usbccgp OriginalConfigurationValue is $override instead of 5 on $registryPath."
    }
}
catch {
    throw "Could not verify Usbccgp OriginalConfigurationValue on the Apple parent: $($_.Exception.Message)"
}

Write-Host "Restarting Apple composite parent once so Usbccgp re-selects configuration 5..."
& pnputil.exe /restart-device $parent.InstanceId
if ($LASTEXITCODE -ne 0) {
    throw "pnputil could not restart the Apple composite parent. Reconnect the Apple device before testing."
}

Start-Sleep -Seconds 3

Write-Host "`n=== Step 2: Install WinUSB on MI_00 ==="
Write-Host "Registering WinUSB control package..."
& pnputil.exe /add-driver $inf /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil /add-driver failed for WinUsbControl.inf with exit code $LASTEXITCODE. Check catalog signing and Windows driver policy."
}

Write-Host "Forcing iPhoneUsbShare WinUSB driver onto MI_00..."
$rebootRequired = $false
[IPhoneUsbShare_NewDev]::InstallForced($hardwareId, $inf, [ref]$rebootRequired) | Out-Null
if ($rebootRequired) {
    Write-Warning "Windows reports that a reboot may be required after the WinUSB driver update."
}

$device = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'USB\VID_05AC&PID_12AB&MI_00\*' } |
    Select-Object -First 1

if (-not $device) {
    throw "Apple MI_00 device was not found after installing the Usbccgp configuration override."
}

Write-Host "MI_00 instance: $($device.InstanceId)"
Write-Host "Restarting MI_00 once to publish the WinUSB interface..."
& pnputil.exe /restart-device $device.InstanceId
if ($LASTEXITCODE -ne 0) {
    Write-Warning "pnputil could not restart MI_00; unplug/replug the Apple device before testing."
}

Write-Host "`nFinal driver state:`n"
& pnputil.exe /enum-devices /instanceid $parent.InstanceId /drivers
& pnputil.exe /enum-devices /instanceid $device.InstanceId /drivers
