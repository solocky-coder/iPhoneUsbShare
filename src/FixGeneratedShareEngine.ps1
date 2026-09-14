$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -Raw -LiteralPath $path
$start = $text.IndexOf('    private async Task BindUsbNcmDriverAsync()')
$endMarker = '    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]'
$end = $text.IndexOf($endMarker, $start)
if ($start -lt 0 -or $end -lt 0) { throw 'FixGeneratedShareEngine: generated BindUsbNcmDriverAsync region not found.' }
$region = $text.Substring($start, $end - $start)

# Normalize accidental backslash-quote pairs emitted by BuildFix4.
$region = $region.Replace('\"', '"')
$region = $region.Replace('$"/enum-devices /instanceid "{target.Id}" /ids /drivers"', '$"/enum-devices /instanceid {target.Id} /ids /drivers"')
$region = $region.Replace('$"/add-driver "{inf}" /install"', '$"/add-driver {inf} /install"')

# SetupDiGetDriverInfoDetail requires a valid fixed header buffer on the first
# call. Passing a NULL detail pointer with size 0 can yield ERROR_INVALID_USER_BUFFER.
# The cbSize is sizeof(SP_DRVINFO_DETAIL_DATA_W), while the supplied buffer may
# be larger because HardwareID is variable length.
$oldDetail = @'
    private static string? GetDriverInfoDetail(IntPtr h, ref SP_DEVINFO_DATA devInfo, ref SP_DRVINFO_DATA driver, out int error)
    {
        error = 0;
        uint requiredSize = 0;
        var first = SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, IntPtr.Zero, 0, out requiredSize);
        if (first)
        {
            error = 0;
            return null;
        }

        var firstError = Marshal.GetLastWin32Error();
        if (firstError != ERROR_INSUFFICIENT_BUFFER || requiredSize < NativeSpDrvInfoDetailSize)
        {
            error = firstError;
            return null;
        }

        var bufferSize = checked((int)Math.Max(requiredSize, (uint)(NativeSpDrvInfoDetailSize + 2)));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, (int)NativeSpDrvInfoDetailSize);
            if (!SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, buffer, (uint)bufferSize, out requiredSize))
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }

            var infOffset = IntPtr.Size == 8 ? 536 : 532;
            var inf = Marshal.PtrToStringUni(IntPtr.Add(buffer, infOffset));
            return string.IsNullOrWhiteSpace(inf) ? null : inf;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static readonly int NativeSpDrvInfoDetailSize = IntPtr.Size == 8 ? 1576 : 1568;
'@
$newDetail = @'
    private static string? GetDriverInfoDetail(IntPtr h, ref SP_DEVINFO_DATA devInfo, ref SP_DRVINFO_DATA driver, out int error)
    {
        error = 0;
        var headerSize = NativeSpDrvInfoDetailSize;
        var header = Marshal.AllocHGlobal(headerSize);
        try
        {
            Marshal.WriteInt32(header, headerSize);
            uint requiredSize = 0;
            if (SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, header, (uint)headerSize, out requiredSize))
            {
                var inf = Marshal.PtrToStringUni(IntPtr.Add(header, NativeSpDrvInfoDetailInfFileNameOffset));
                return string.IsNullOrWhiteSpace(inf) ? null : inf;
            }

            var firstError = Marshal.GetLastWin32Error();
            if (firstError != ERROR_INSUFFICIENT_BUFFER || requiredSize < (uint)headerSize)
            {
                error = firstError;
                return null;
            }

            var bufferSize = checked((int)Math.Max(requiredSize, (uint)(headerSize + 2)));
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                Marshal.WriteInt32(buffer, headerSize);
                if (!SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, buffer, (uint)bufferSize, out requiredSize))
                {
                    error = Marshal.GetLastWin32Error();
                    return null;
                }

                var inf = Marshal.PtrToStringUni(IntPtr.Add(buffer, NativeSpDrvInfoDetailInfFileNameOffset));
                return string.IsNullOrWhiteSpace(inf) ? null : inf;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Marshal.FreeHGlobal(header); }
    }

    // SP_DRVINFO_DETAIL_DATA_W layout on Windows: cbSize, FILETIME, two DWORDs,
    // pointer-sized Reserved, SectionName[256], InfFileName[MAX_PATH].
    private static readonly int NativeSpDrvInfoDetailSize = IntPtr.Size == 8 ? 1576 : 1564;
    private static readonly int NativeSpDrvInfoDetailInfFileNameOffset = IntPtr.Size == 8 ? 544 : 532;
'@
if ($region.Contains($oldDetail)) {
    $region = $region.Replace($oldDetail, $newDetail)
} else {
    throw 'FixGeneratedShareEngine: expected GetDriverInfoDetail implementation not found.'
}

# SetupAPI can exclude a package from the driver list because it is not the
# current/best-ranked driver. Ask SetupAPI to include excluded candidates so the
# already-registered Microsoft UsbNcm package can be explicitly selected.
$buildNeedle = '                if (!SetupDiBuildDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER))'
$buildReplacement = @'
                var installParams = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>() };
                if (SetupDiGetDeviceInstallParams(h, ref devInfo, ref installParams))
                {
                    installParams.FlagsEx |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS;
                    if (!SetupDiSetDeviceInstallParams(h, ref devInfo, ref installParams))
                    {
                        error = (uint)Marshal.GetLastWin32Error();
                        return false;
                    }
                    WriteLog("SetupAPI enabled excluded-driver enumeration for the target devnode.");
                }
                else
                {
                    error = (uint)Marshal.GetLastWin32Error();
                    return false;
                }

                if (!SetupDiBuildDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER))
'@
if (-not $region.Contains($buildNeedle)) { throw 'FixGeneratedShareEngine: SetupDiBuildDriverInfoList anchor not found.' }
$region = $region.Replace($buildNeedle, $buildReplacement)

$structNeedle = @'
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct SP_DEVINSTALL_PARAMS
    {
        public uint cbSize;
        public uint Flags;
        public uint FlagsEx;
        public IntPtr HwndParent;
        public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext;
        public IntPtr FileQueue;
        public UIntPtr CallInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }
'@
$structReplacement = @'
    // Matches the native SP_DEVINSTALL_PARAMS_W layout. Do not use Pack=4 here:
    // on x64 the pointer fields are naturally 8-byte aligned, and SetupAPI
    // rejects a smaller/mislaid-out buffer with ERROR_INVALID_USER_BUFFER (1784).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINSTALL_PARAMS
    {
        public uint cbSize;
        public uint Flags;
        public uint FlagsEx;
        public IntPtr HwndParent;
        public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext;
        public IntPtr FileQueue;
        public UIntPtr ClassInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }
'@
if (-not $region.Contains($structNeedle)) { throw 'FixGeneratedShareEngine: SP_DEVINSTALL_PARAMS anchor not found.' }
$region = $region.Replace($structNeedle, $structReplacement)

$constNeedle = '    private const uint DIF_INSTALLDEVICE = 0x00000001;'
$constReplacement = @'
    private const uint DIF_INSTALLDEVICE = 0x00000001;
    private const uint DI_FLAGSEX_ALLOWEXCLUDEDDRVS = 0x00000800;
'@
$region = $region.Replace($constNeedle, $constReplacement)

$dllNeedle = '    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList'
$dllReplacement = @'
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DEVINSTALL_PARAMS deviceInstallParams);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetDeviceInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DEVINSTALL_PARAMS deviceInstallParams);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList
'@
if (-not $region.Contains($dllNeedle)) { throw 'FixGeneratedShareEngine: SetupAPI DLL anchor not found.' }
$region = $region.Replace($dllNeedle, $dllReplacement)

$text = $text.Substring(0, $start) + $region + $text.Substring($end)
Set-Content -LiteralPath $path -Value $text -Encoding UTF8
