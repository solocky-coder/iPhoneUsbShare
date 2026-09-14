$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -Raw -LiteralPath $path
$start = $text.IndexOf('    private async Task BindUsbNcmDriverAsync()')
$endMarker = '    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]'
$end = $text.IndexOf($endMarker, $start)
if ($start -lt 0 -or $end -lt 0) { throw 'BuildFix4: BindUsbNcmDriverAsync anchor not found.' }
$replacement = @'
    private async Task BindUsbNcmDriverAsync()
    {
        List<PnpDevice> controls = new();
        var restartAttempted = false;
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            controls = FindPnP("VID_05AC&PID_12AB", null)
                .Where(d => d.Id.Contains("&MI_02\", StringComparison.OrdinalIgnoreCase) || d.Id.Contains("&MI_04\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (controls.Count > 0)
            {
                foreach (var d in controls) WriteLog($"NCM control interface: {d.Id} | {d.Name}");
                break;
            }
            if (attempt == 1 || attempt % 5 == 0)
            {
                WriteLog($"Waiting for iPad NCM control interfaces (attempt {attempt}/30)…");
                WriteLog($"Current USB identity: {UsbNative.GetDeviceId() ?? "unreachable"}; mode: {await UsbNative.GetModeAsync() ?? "unreachable"}");
                WriteLog($"Current Apple PnP device: {FindAppleDevice()?.Id ?? "not found"}");
            }
            if (!restartAttempted && attempt == 6)
            {
                restartAttempted = true;
                var apple = FindAppleDevice();
                if (apple is not null)
                {
                    WriteLog($"NCM interfaces still absent; restarting Apple device once: {apple.Id}");
                    try { RestartDevice(apple.Id); WriteLog("Apple device restart requested; continuing NCM interface polling."); }
                    catch (Exception ex) { WriteLog($"Apple device restart for NCM re-enumeration failed: {ex.Message}"); }
                }
            }
            await Task.Delay(500);
        }
        if (controls.Count == 0)
        {
            WriteLog("No iPad NCM control interface (MI_02/MI_04) visible after re-enumeration polling.");
            WriteLog($"Final USB identity: {UsbNative.GetDeviceId() ?? "unreachable"}; mode: {await UsbNative.GetModeAsync() ?? "unreachable"}");
            throw new InvalidOperationException("Apple accepted CDC-NCM mode, but Windows did not expose MI_02/MI_04. The device did not finish CDC-NCM re-enumeration.");
        }

        var target = controls.FirstOrDefault(d => d.Id.Contains("&MI_02\", StringComparison.OrdinalIgnoreCase))
                     ?? controls.FirstOrDefault(d => d.Id.Contains("&MI_04\", StringComparison.OrdinalIgnoreCase));
        if (target is null) return;
        WriteLog($"Selected NCM control interface for UsbNcm: {target.Id} | {target.Name}");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(windows, "INF", "usbncm.inf"),
            Path.Combine(windows, "INF", "netncm.inf")
        }.Where(File.Exists).ToList();
        if (candidates.Count == 0)
        {
            try { candidates = Directory.EnumerateFiles(Path.Combine(windows, "System32", "DriverStore", "FileRepository"), "usbncm.inf", SearchOption.AllDirectories).ToList(); }
            catch { }
        }
        var inf = candidates.FirstOrDefault();
        WriteLog($"Windows NCM INF: {inf ?? "not found"}");
        if (inf is null) throw new InvalidOperationException("Windows UsbNcm INF was not found.");

        var beforePnp = RunAllowRestart("pnputil.exe", $"/enum-devices /instanceid \"{target.Id}\" /ids /drivers");
        WriteLog($"NCM PnP diagnostic before ownership change exit code: {beforePnp.ExitCode}");
        if (!string.IsNullOrWhiteSpace(beforePnp.Output)) WriteLog("NCM PnP diagnostic before ownership change output: " + beforePnp.Output.Trim());
        if (!string.IsNullOrWhiteSpace(beforePnp.Error)) WriteLog("NCM PnP diagnostic before ownership change error: " + beforePnp.Error.Trim());

        var add = RunAllowRestart("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"UsbNcm package registration exit code: {add.ExitCode}");
        if (!string.IsNullOrWhiteSpace(add.Output)) WriteLog($"UsbNcm package output: {add.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(add.Error)) WriteLog($"UsbNcm package error: {add.Error.Trim()}");

        var ownershipChanged = InstallSelectedNcmDriver(target.Id, inf, out var setupError);
        WriteLog($"UsbNcm SetupAPI ownership change {target.Id}: {(ownershipChanged ? "success" : "failed")}, Win32Error={setupError}");

        var postPnp = RunAllowRestart("pnputil.exe", $"/enum-devices /instanceid \"{target.Id}\" /ids /drivers");
        WriteLog($"NCM PnP diagnostic after ownership change exit code: {postPnp.ExitCode}");
        if (!string.IsNullOrWhiteSpace(postPnp.Output)) WriteLog("NCM PnP diagnostic after ownership change output: " + postPnp.Output.Trim());
        if (!string.IsNullOrWhiteSpace(postPnp.Error)) WriteLog("NCM PnP diagnostic after ownership change error: " + postPnp.Error.Trim());

        if (!ownershipChanged) throw new InvalidOperationException($"Windows did not install UsbNcm on MI_02 (SetupAPI error {setupError}).");

        await Task.Delay(1500);
        try { RestartDevice(target.Id); WriteLog($"NCM control device restart requested: {target.Id}"); }
        catch (Exception ex) { WriteLog($"NCM control device restart failed: {ex.Message}"); }
        await Task.Delay(1500);
        var state = FindPnP(target.Id, null).FirstOrDefault();
        WriteLog($"Selected NCM control state after ownership change: {state?.Name ?? "not found"}");
    }

    private bool InstallSelectedNcmDriver(string instanceId, string infPath, out uint error)
    {
        error = 0;
        var emptyGuid = Guid.Empty;
        var h = SetupDiGetClassDevs(ref emptyGuid, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (h == INVALID_HANDLE_VALUE)
        {
            error = (uint)Marshal.GetLastWin32Error();
            return false;
        }
        try
        {
            for (uint index = 0; ; index++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(h, index, ref devInfo))
                {
                    var e = Marshal.GetLastWin32Error();
                    if (e == ERROR_NO_MORE_ITEMS) break;
                    error = (uint)e;
                    return false;
                }
                var id = GetDeviceInstanceId(h, ref devInfo);
                if (!string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                WriteLog($"SetupAPI found target devnode: {id}");

                if (!SetupDiBuildDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER))
                {
                    error = (uint)Marshal.GetLastWin32Error();
                    return false;
                }
                try
                {
                    var candidateCount = 0;
                    for (uint driverIndex = 0; ; driverIndex++)
                    {
                        var driver = new SP_DRVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA>() };
                        if (!SetupDiEnumDriverInfo(h, ref devInfo, SPDIT_CLASSDRIVER, driverIndex, ref driver))
                        {
                            var e = Marshal.GetLastWin32Error();
                            if (e == ERROR_NO_MORE_ITEMS) break;
                            error = (uint)e;
                            return false;
                        }
                        candidateCount++;
                        var detail = GetDriverInfoDetail(h, ref devInfo, ref driver, out var detailError);
                        if (detail is null)
                        {
                            WriteLog($"SetupAPI class candidate {driverIndex}: detail lookup failed, Win32Error={detailError}");
                            continue;
                        }
                        var detailInf = detail;
                        WriteLog($"SetupAPI class candidate {driverIndex}: {detailInf} | {driver.Description} | provider={driver.ProviderName}");
                        if (!string.Equals(Path.GetFileName(detailInf), Path.GetFileName(infPath), StringComparison.OrdinalIgnoreCase)) continue;

                        if (!SetupDiSetSelectedDriver(h, ref devInfo, ref driver))
                        {
                            error = (uint)Marshal.GetLastWin32Error();
                            return false;
                        }
                        WriteLog($"SetupAPI selected UsbNcm driver: {detailInf}");
                        if (!SetupDiCallClassInstaller(DIF_INSTALLDEVICE, h, ref devInfo))
                        {
                            error = (uint)Marshal.GetLastWin32Error();
                            return false;
                        }
                        WriteLog("SetupAPI class installer installed the selected UsbNcm driver on the target devnode.");
                        return true;
                    }
                    WriteLog($"SetupAPI class-driver enumeration ended with {candidateCount} candidate(s).");
                    error = ERROR_NO_MORE_ITEMS;
                    return false;
                }
                finally { SetupDiDestroyDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER); }
            }
            error = ERROR_NO_SUCH_DEVINST;
            return false;
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
    }

    private static string? GetDeviceInstanceId(IntPtr h, ref SP_DEVINFO_DATA devInfo)
    {
        var buffer = new StringBuilder(512);
        if (!SetupDiGetDeviceInstanceId(h, ref devInfo, buffer, buffer.Capacity, out _)) return null;
        return buffer.ToString();
    }

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
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint SPDIT_CLASSDRIVER = 0x00000001;
    private const uint DIF_INSTALLDEVICE = 0x00000001;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_NO_SUCH_DEVINST = 433;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ManufacturerName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
        public long DriverDate;
        public ulong DriverVersion;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiBuildDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDriverInfo(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType, uint memberIndex, ref SP_DRVINFO_DATA driverInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDriverInfoDetail(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData, IntPtr driverInfoDetailData, uint driverInfoDetailDataSize, out uint requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetSelectedDriver(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

'@
# The replacement is a single-quoted here-string, so C# double quotes do not need
# PowerShell escaping. Normalize any accidental backslash-quote pairs before
# writing the generated C# source.
$replacement = $replacement.Replace('\"', '"')
$text = $text.Substring(0, $start) + $replacement + $text.Substring($end)
Set-Content -LiteralPath $path -Value $text -Encoding UTF8
