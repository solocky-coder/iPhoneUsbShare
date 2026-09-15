# Run elevated on the target Windows 10 x64 machine.
# Installs the iPhoneUsbShare WinUSB control driver on Apple MI_00.
# The package must contain WinUsbControl.cat signed for the target Windows
# driver-signing policy. The bring-up path below can force a staged package
# during development; production builds must use a properly signed catalog.

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
    throw "WinUsbControl.cat not found: $cat"
}

$hardwareId = 'USB\VID_05AC&PID_12AB&MI_00'
Write-Host "WinUSB control package: $inf"
Write-Host "Target hardware ID: $hardwareId"

Write-Host "Registering WinUSB control package..."
& pnputil.exe /add-driver $inf /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil /add-driver failed with exit code $LASTEXITCODE. Check catalog signing and Windows driver policy."
}

# PnPUtil intentionally refuses to force a lower-ranked package. On the
# observed Apple MI_00 interface, Microsoft's signed WPD/MTP package outranks
# our unsigned development catalog. Use the documented NewDev update API with
# INSTALLFLAG_FORCE for the explicit development bring-up path instead.
# This API performs the actual PnP driver update rather than merely selecting
# a SetupAPI driver node, so the device setup class can be changed as part of
# the installation.
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

Write-Host "Forcing iPhoneUsbShare WinUSB driver onto MI_00..."
$rebootRequired = $false
[IPhoneUsbShare_NewDev]::InstallForced($hardwareId, $inf, [ref]$rebootRequired) | Out-Null

if ($rebootRequired) {
    Write-Warning "Windows reports that a reboot may be required after the driver update."
}

$device = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'USB\VID_05AC&PID_12AB&MI_00\*' } |
    Select-Object -First 1

if (-not $device) {
    throw "Apple MI_00 device was not found after driver installation."
}

Write-Host "MI_00 instance: $($device.InstanceId)"
Write-Host "Restarting MI_00..."
& pnputil.exe /restart-device $device.InstanceId
if ($LASTEXITCODE -ne 0) {
    Write-Warning "pnputil could not restart MI_00; unplug/replug the Apple device before testing."
}

Write-Host "`nFinal driver state:`n"
& pnputil.exe /enum-devices /instanceid $device.InstanceId /drivers
