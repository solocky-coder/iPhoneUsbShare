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

# PnPUtil will not force a lower-ranked package. On this Windows 10 target,
# Microsoft's signed WPD driver is a compatible match for MI_00 and therefore
# outranks an unsigned development catalog even though our INF has the exact
# MI_00 hardware ID. Use SetupAPI to explicitly select the staged iPhoneUsbShare
# package for this exact devnode. Once the catalog is Microsoft-signed, normal
# PnP ranking will select the package automatically.
Write-Host "Selecting iPhoneUsbShare WinUSB driver explicitly for MI_00..."

if (-not ('IPhoneUsbShare_SetupApi' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class IPhoneUsbShare_SetupApi
{
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint SPDIT_COMPATDRIVER = 0x00000002;
    private const uint DIF_INSTALLDEVICE = 0x00000002;
    private const uint ERROR_NO_MORE_ITEMS = 259;
    private const int LINE_LEN = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DATA
    {
        public uint cbSize;
        public uint DriverType;
        public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LINE_LEN)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LINE_LEN)] public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LINE_LEN)] public string ProviderName;
        public System.Runtime.InteropServices.ComTypes.FILETIME DriverDate;
        public ulong DriverVersion;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr ClassGuid,
        string Enumerator,
        IntPtr hwndParent,
        uint Flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfo(
        IntPtr DeviceInfoSet,
        string DeviceInstanceId,
        IntPtr hwndParent,
        uint OpenFlags,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint DriverType);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfo(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint DriverType,
        uint MemberIndex,
        ref SP_DRVINFO_DATA DriverInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetSelectedDriver(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_DRVINFO_DATA DriverInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction,
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    public static void InstallSelected(string instanceId)
    {
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == new IntPtr(-1))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");

        try
        {
            SP_DEVINFO_DATA device = new SP_DEVINFO_DATA();
            device.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref device))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetupDiOpenDeviceInfo failed");

            if (!SetupDiBuildDriverInfoList(set, ref device, SPDIT_COMPATDRIVER))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetupDiBuildDriverInfoList failed");

            SP_DRVINFO_DATA selected = new SP_DRVINFO_DATA();
            bool found = false;
            for (uint i = 0; ; i++)
            {
                SP_DRVINFO_DATA info = new SP_DRVINFO_DATA();
                info.cbSize = (uint)Marshal.SizeOf(typeof(SP_DRVINFO_DATA));
                if (!SetupDiEnumDriverInfo(set, ref device, SPDIT_COMPATDRIVER, i, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if ((uint)error == ERROR_NO_MORE_ITEMS) break;
                    throw new System.ComponentModel.Win32Exception(error, "SetupDiEnumDriverInfo failed");
                }

                Console.WriteLine("Candidate: " + info.ProviderName + " | " + info.Description);
                if (string.Equals(info.ProviderName, "iPhoneUsbShare", StringComparison.OrdinalIgnoreCase))
                {
                    selected = info;
                    found = true;
                    break;
                }
            }

            if (!found)
                throw new InvalidOperationException("The staged iPhoneUsbShare driver was not found in the compatible-driver list for MI_00.");

            if (!SetupDiSetSelectedDriver(set, ref device, ref selected))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetupDiSetSelectedDriver failed");

            Console.WriteLine("Selected: " + selected.ProviderName + " | " + selected.Description);

            if (!SetupDiCallClassInstaller(DIF_INSTALLDEVICE, set, ref device))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCallClassInstaller(DIF_INSTALLDEVICE) failed");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
'@
}

$device = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'USB\VID_05AC&PID_12AB&MI_00\*' } |
    Select-Object -First 1

if (-not $device) {
    throw "Apple MI_00 device was not found. Connect the iPad/iPhone and retry."
}

Write-Host "MI_00 instance: $($device.InstanceId)"
[IPhoneUsbShare_SetupApi]::InstallSelected($device.InstanceId)

Write-Host "Restarting MI_00..."
& pnputil.exe /restart-device $device.InstanceId
if ($LASTEXITCODE -ne 0) {
    Write-Warning "pnputil could not restart MI_00; unplug/replug the Apple device and continue."
}

Write-Host "`nFinal driver state:`n"
& pnputil.exe /enum-devices /instanceid $device.InstanceId /drivers
