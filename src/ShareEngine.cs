using Microsoft.Win32;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

public sealed class ShareEngine
{
    public event EventHandler<string>? Log;
    private const string Vendor = "05AC";
    private const string SafeIndexValue = "2";
    private const string NcmIndexValue = "4";
    private const string Subnet = "192.168.137.";
    private const string LibUsbZipUrl = "https://github.com/mcuee/libusb-win32/releases/download/release_1.4.0.2/libusb-win32-bin-1.4.0.2.zip";
    private const string LibUsbZipSha256 = "00004c92cdb99be36e17fb2377165eb97e63b48ba895bfc04a642ea9c3e26d94";
    private static readonly object LogFileLock = new();
    private string AppDir => AppContext.BaseDirectory;
    private string ActivityLogPath => Path.Combine(AppDir, "ActivityLog.txt");
    private string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iPhoneUsbShare");

    public ShareEngine()
    {
        WriteLog("============================================================");
        WriteLog("iPhoneUsbShare session started");
        WriteLog($"Application directory: {AppDir}");
        WriteLog($"Activity log: {ActivityLogPath}");
    }

    public void WriteLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try { lock (LogFileLock) File.AppendAllText(ActivityLogPath, line + Environment.NewLine, new UTF8Encoding(false)); } catch { }
        Log?.Invoke(this, message);
    }

    public async Task EnsurePrerequisitesAsync()
    {
        Directory.CreateDirectory(CacheDir);
        if (UsbNative.IsReachable()) return;
        WriteLog("Installing the USB filter driver (one-time)…");
        var arch = Environment.Is64BitOperatingSystem ? "amd64" : "x86";
        var zip = Path.Combine(CacheDir, "libusb-win32.zip");
        var extracted = Path.Combine(CacheDir, "libusb-win32-bin-1.4.0.2");
        if (!File.Exists(zip))
        {
            using var http = new HttpClient();
            await using var src = await http.GetStreamAsync(LibUsbZipUrl);
            await using var dst = File.Create(zip);
            await src.CopyToAsync(dst);
        }
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zip))).ToLowerInvariant();
        if (hash != LibUsbZipSha256) throw new InvalidOperationException($"libusb-win32 download failed SHA-256 verification ({hash}).");
        if (!Directory.Exists(extracted)) ZipFile.ExtractToDirectory(zip, CacheDir, overwriteFiles: true);
        var baseDir = extracted;
        var installer = Path.Combine(baseDir, "bin", arch, "install-filter.exe");
        var packageDll = Path.Combine(baseDir, "bin", arch, "libusb0.dll");
        var sys = Path.Combine(baseDir, "bin", arch, "libusb0.sys");
        if (!File.Exists(installer) || !File.Exists(packageDll) || !File.Exists(sys)) throw new InvalidOperationException("The libusb-win32 package is missing the required files.");
        if (!File.Exists(Path.Combine(AppDir, "libusb0.dll"))) throw new InvalidOperationException("libusb0.dll is missing beside iPhoneUsbShare.exe. Re-extract the complete ZIP and try again.");
        var phone = FindAppleDevice() ?? throw new InvalidOperationException("Connect an iPhone or iPad with a data-capable USB cable and try again.");
        WriteLog($"Apple USB device: {phone.Id}");
        var installResult = RunAllowRestart(installer, $"install --device=USB\\VID_{Vendor}&PID_{GetApplePid(phone.Id)}");
        WriteLog($"libusb filter installer exit code: {installResult.ExitCode}");
        if (!string.IsNullOrWhiteSpace(installResult.Output)) WriteLog($"libusb installer output: {installResult.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(installResult.Error)) WriteLog($"libusb installer error: {installResult.Error.Trim()}");
        if (installResult.ExitCode != 0) throw new InvalidOperationException($"install-filter.exe failed ({installResult.ExitCode}): {installResult.Error}");
        var sysDst = Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys");
        if (!File.Exists(sysDst)) { File.Copy(sys, sysDst, false); WriteLog($"Copied libusb0.sys to {sysDst}"); }
        else WriteLog($"libusb0.sys already present: {sysDst}");
        try
        {
            using var svc = new System.ServiceProcess.ServiceController("libusb0");
            WriteLog($"libusb0 service status: {svc.Status}");
            if (svc.Status != System.ServiceProcess.ServiceControllerStatus.Running) { svc.Start(); svc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)); WriteLog("libusb0 service started."); }
        }
        catch (Exception ex) { WriteLog($"libusb0 service start check: {ex.Message}"); }
        ConfigureUsbDevice(phone);
        WriteLog("USB driver setup complete.");
        await WaitUntil(() => UsbNative.IsReachable(), 15, "Apple USB filter driver");
    }

    public async Task StartAsync()
    {
        await EnsurePrerequisitesAsync();
        var phone = FindAppleDevice() ?? throw new InvalidOperationException("iPhone/iPad not found.");
        WriteLog($"Apple device found: {phone.Id} ({phone.Name})");
        DisablePhotoInterfaces();
        var wifi = FindWifi() ?? throw new InvalidOperationException("No connected Wi-Fi adapter found.");
        WriteLog($"Internet source: {wifi.Name}");
        var adapter = FindPhoneAdapter();
        if (adapter is not null && adapter.OperationalStatus == OperationalStatus.Up)
        {
            WriteLog($"USB Ethernet already available: {adapter.Name}");
        }
        else
        {
            SetConfig(phone.Id, SafeIndexValue, "0");
            WriteLog("Set Apple USB configuration to safe mode (2).");
            RestartDevice(phone.Id);
            await WaitUntil(() => FindAppleDevice() is not null, 25, "Apple device to re-enumerate");
            DisablePhotoInterfaces();
            WriteLog("Waiting for Apple USB control interface…");
            string? mode = null;
            for (var i = 0; i < 20; i++)
            {
                mode = await UsbNative.GetModeAsync();
                WriteLog($"GET_MODE attempt {i + 1}/20: {(mode ?? "unreachable")}");
                if (mode is not null) break;
                await Task.Delay(1000);
            }
            if (mode is null) throw new InvalidOperationException("Apple USB control interface is unreachable after the device restart. The libusb filter did not reattach; unplug/replug the iPad and try again.");
            WriteLog($"Apple USB mode: {mode}");

            if (mode == "5:3:3:0" || mode == "5:3:3")
            {
                WriteLog("Apple is already in CDC-NCM direct mode (5); skipping configuration 4 and SET_MODE(3).");
            }
            else
            {
                if (mode != "3:3:3:0" && mode != "3:3:3")
                    throw new InvalidOperationException($"Unexpected Apple USB mode: {mode}.");
                SetConfig(phone.Id, NcmIndexValue, SafeIndexValue);
                WriteLog("Set Apple USB configuration to CDC-NCM mode (4).");
                var accepted = await UsbNative.SetModeAsync(3);
                WriteLog($"SET_MODE(3) result: {(accepted ? "accepted" : "rejected/failed")}");
                if (!accepted)
                {
                    SetConfig(phone.Id, SafeIndexValue, "0");
                    throw new InvalidOperationException("The Apple device rejected the CDC-NCM mode switch. Unplug/replug and try again.");
                }
                WriteLog("Apple device accepted CDC-NCM mode; waiting for USB Ethernet…");
            }

            // The iPad exposes two CDC-NCM pairs in configuration 5:
            // control MI_02 -> data MI_03 and control MI_04 -> data MI_05.
            // The user's Windows 10 machine has the legacy Apple Ethernet
            // driver attached to MI_02 (with a warning icon), so deliberately
            // bind Microsoft's UsbNcm driver to the unclaimed MI_04 control
            // interface instead of the MI_05 data interface.
            await BindUsbNcmDriverAsync();
            try
            {
                await WaitUntil(() => FindPhoneAdapter()?.OperationalStatus == OperationalStatus.Up, 15, "USB Ethernet adapter");
            }
            catch
            {
                // A fresh iOS connection can perform a later USB reset. Re-arm
                // the safe configuration and run the mode sequence once more.
                WriteLog("USB Ethernet did not remain available on the first pass; restoring safe configuration and retrying the mode switch once.");
                var current = FindAppleDevice();
                if (current is null) throw;
                SetConfig(current.Id, SafeIndexValue, "0");
                RestartDevice(current.Id);
                await WaitUntil(() => FindAppleDevice() is not null, 25, "Apple device after NCM retry reset");
                DisablePhotoInterfaces();
                var retryMode = await UsbNative.GetModeAsync();
                WriteLog($"Retry GET_MODE: {retryMode ?? "unreachable"}");
                if (retryMode != "3:3:3:0" && retryMode != "3:3:3") throw new InvalidOperationException("Apple USB device did not return to safe mode for the NCM retry.");
                SetConfig(current.Id, NcmIndexValue, SafeIndexValue);
                if (!await UsbNative.SetModeAsync(3)) throw new InvalidOperationException("Apple device rejected the CDC-NCM retry mode switch.");
                await BindUsbNcmDriverAsync();
                await WaitUntil(() => FindPhoneAdapter()?.OperationalStatus == OperationalStatus.Up, 30, "USB Ethernet adapter after retry");
            }
            adapter = FindPhoneAdapter() ?? throw new InvalidOperationException("USB Ethernet adapter did not start.");
            WriteLog($"USB Ethernet adapter is up: {adapter.Name}");
            DisablePhotoInterfaces();
        }
        await ConfigureIcsAsync(wifi.Name, adapter.Name);
        WriteLog("Waiting for DHCP lease…");
        await WaitUntil(() => FindLease(adapter.Name) is not null, 30, "phone DHCP lease");
        WriteLog($"DHCP lease acquired: {FindLease(adapter.Name) ?? "none"}");
    }

    public Task StopAsync()
    {
        try { if (FindPhoneAdapter() is not null) DisableAllIcs(); var phone = FindAppleDevice(); if (phone is not null) SetConfig(phone.Id, SafeIndexValue, "0"); WriteLog("Sharing stopped and Apple USB configuration restored."); }
        catch (Exception ex) { WriteLog($"Stop cleanup: {ex.Message}"); }
        return Task.CompletedTask;
    }

    public async Task<Status> GetStatusAsync()
    {
        var p = FindAppleDevice();
        var a = FindPhoneAdapter();
        var lease = a is null ? null : FindLease(a.Name);
        var sharing = a is not null && IsIcsEnabled(a.Name);
        var (rx, tx) = a is null ? (0d, 0d) : GetRates(a.Name);
        return await Task.FromResult(new Status(p is not null, p?.Name ?? "Apple device", a?.Name, a?.OperationalStatus.ToString() ?? "—", sharing, lease, rx, tx));
    }

    public async Task<string> DiagnosticsAsync()
    {
        WriteLog("Running diagnostics…");
        var sb = new StringBuilder();
        var p = FindAppleDevice(); var a = FindPhoneAdapter();
        sb.AppendLine($"Apple device: {(p is null ? "not connected" : p.Name)}");
        sb.AppendLine($"Apple PnP ID: {p?.Id ?? "—"}");
        sb.AppendLine($"USB identity: {UsbNative.GetDeviceId() ?? "unreachable"}");
        sb.AppendLine($"USB mode: {await UsbNative.GetModeAsync() ?? "unreachable"}");
        sb.AppendLine($"USB Ethernet: {a?.Name ?? "not present"} [{a?.OperationalStatus.ToString() ?? "—"}]");
        sb.AppendLine($"Lease: {(a is null ? "—" : FindLease(a.Name) ?? "none")}");
        sb.AppendLine($"Wi-Fi: {FindWifi()?.Name ?? "none"}");
        WriteLog("Diagnostics result: " + sb.ToString().Replace(Environment.NewLine, " | ").Trim());
        return sb.ToString();
    }

    private async Task BindUsbNcmDriverAsync()
    {
        // Do not identify the NCM function from a Windows-generated CDC_0D
        // hardware ID. Windows 10 can expose the same CDC union as plain
        // VID/PID/MI child nodes. Identify the tethering function from the
        // actual USB descriptors, then map its interface number to the PnP
        // child. The first NCM control interface with an interrupt endpoint
        // is the tethering function; the later NCM function is RemoteXPC.
        var controlInterfaces = await UsbNative.GetNcmControlInterfacesAsync();
        if (controlInterfaces.Length == 0)
        {
            WriteLog("USB descriptors expose no CDC-NCM control interface after mode 5.");
            LogAppleInterfaces();
            return;
        }

        foreach (var n in controlInterfaces)
            WriteLog($"USB descriptor NCM control interface: {n}");

        var appleChildren = FindPnP("USB\\VID_05AC&PID_", null).ToList();
        var targets = controlInterfaces
            .Select(n => appleChildren.FirstOrDefault(d => TryGetInterfaceNumber(d.Id, out var mi) && mi == n))
            .Where(d => d is not null)
            .Cast<PnpDevice>()
            .ToList();

        foreach (var target in targets)
            LogPnpDriverState(target.Id, "NCM candidate");

        if (targets.Count == 0)
        {
            WriteLog("CDC-NCM descriptors were present, but Windows exposed no matching Apple MI child nodes.");
            LogAppleInterfaces();
            return;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidatesInf = new[]
        {
            Path.Combine(windows, "INF", "usbncm.inf"),
            Path.Combine(windows, "INF", "netncm.inf")
        }.Where(File.Exists).ToList();
        if (candidatesInf.Count == 0)
        {
            try
            {
                candidatesInf = Directory.EnumerateFiles(
                    Path.Combine(windows, "System32", "DriverStore", "FileRepository"),
                    "usbncm.inf", SearchOption.AllDirectories).ToList();
            }
            catch { }
        }

        var inf = candidatesInf.FirstOrDefault();
        WriteLog($"Windows NCM INF: {inf ?? "not found"}");
        if (inf is null)
        {
            WriteLog("No Microsoft UsbNcm INF is installed on this Windows system; leaving the existing Apple NCM driver untouched.");
            return;
        }

        var add = RunAllowRestart("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"UsbNcm package registration exit code: {add.ExitCode}");
        if (!string.IsNullOrWhiteSpace(add.Output)) WriteLog($"UsbNcm package output: {add.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(add.Error)) WriteLog($"UsbNcm package error: {add.Error.Trim()}");

        // Prefer the descriptor-identified tethering function. If Windows has
        // more than one NCM child, try them in descriptor order; the RemoteXPC
        // function normally has no interrupt endpoint and will simply fail to
        // become a NIC, after which the tethering function is retained.
        foreach (var target in targets)
        {
            WriteLog($"Selecting Microsoft UsbNcm for Apple NCM interface: {target.Id} | {target.Name}");
            var changed = InstallSelectedNcmDriver(target.Id, inf, out var setupError);
            WriteLog($"UsbNcm SetupAPI driver selection {target.Id}: {(changed ? "success" : "failed")}, Win32Error={setupError}");
            LogPnpDriverState(target.Id, "after UsbNcm selection");
            if (!changed) continue;

            try { RestartDevice(target.Id); } catch (Exception ex) { WriteLog($"NCM child restart: {ex.Message}"); }
            await Task.Delay(2000);
            LogPnpDriverState(target.Id, "after NCM child restart");

            var adapter = FindPhoneAdapter();
            if (adapter?.OperationalStatus == OperationalStatus.Up)
            {
                WriteLog($"UsbNcm produced a usable adapter: {adapter.Name}");
                return;
            }
            WriteLog("Selected NCM function did not produce an active network adapter; trying the next descriptor-identified NCM function.");
        }
    }

    private static void LogPnpDriverState(string instanceId, string prefix)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID, Name, Service, DriverVersion, Manufacturer, ConfigManagerErrorCode, Status, PNPClass FROM Win32_PnPEntity");
            foreach (ManagementObject o in searcher.Get())
            {
                var id = o["PNPDeviceID"]?.ToString() ?? "";
                if (!id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                WriteStaticLog($"{prefix}: id={id} | name={o["Name"]} | class={o["PNPClass"]} | service={o["Service"]} | driver={o["DriverVersion"]} | manufacturer={o["Manufacturer"]} | configError={o["ConfigManagerErrorCode"]} | status={o["Status"]}");
                return;
            }
        }
        catch (Exception ex) { WriteStaticLog($"{prefix}: driver-state query failed: {ex.Message}"); }
    }

    private static void WriteStaticLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try { lock (LogFileLock) File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ActivityLog.txt"), line + Environment.NewLine, new UTF8Encoding(false)); } catch { }
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

                if (!SetupDiBuildDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER))
                {
                    error = (uint)Marshal.GetLastWin32Error();
                    return false;
                }
                try
                {
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
                        var detail = GetDriverInfoDetail(h, ref devInfo, ref driver, out _);
                        if (detail is null) continue;
                        WriteStaticLog($"SetupAPI candidate for {instanceId}: {detail} | {driver.Description} | provider={driver.ProviderName}");
                        if (!string.Equals(Path.GetFileName(detail), Path.GetFileName(infPath), StringComparison.OrdinalIgnoreCase)) continue;
                        if (!SetupDiSetSelectedDriver(h, ref devInfo, ref driver))
                        {
                            error = (uint)Marshal.GetLastWin32Error();
                            return false;
                        }
                        if (!SetupDiCallClassInstaller(DIF_INSTALLDEVICE, h, ref devInfo))
                        {
                            error = (uint)Marshal.GetLastWin32Error();
                            return false;
                        }
                        return true;
                    }
                }
                finally { SetupDiDestroyDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER); }
                error = ERROR_NO_MORE_ITEMS;
                return false;
            }
            error = ERROR_NO_SUCH_DEVINST;
            return false;
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
    }

    private static string? GetDeviceInstanceId(IntPtr h, ref SP_DEVINFO_DATA devInfo)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(h, ref devInfo, buffer, buffer.Capacity, out _) ? buffer.ToString() : null;
    }

    private static string? GetDriverInfoDetail(IntPtr h, ref SP_DEVINFO_DATA devInfo, ref SP_DRVINFO_DATA driver, out int error)
    {
        error = 0;
        uint requiredSize = 0;
        SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, IntPtr.Zero, 0, out requiredSize);
        var firstError = Marshal.GetLastWin32Error();
        if (firstError != ERROR_INSUFFICIENT_BUFFER || requiredSize < NativeSpDrvInfoDetailSize) { error = firstError; return null; }
        var bufferSize = checked((int)Math.Max(requiredSize, (uint)(NativeSpDrvInfoDetailSize + 2)));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, (int)NativeSpDrvInfoDetailSize);
            if (!SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, buffer, (uint)bufferSize, out requiredSize)) { error = Marshal.GetLastWin32Error(); return null; }
            var infOffset = IntPtr.Size == 8 ? 536 : 532;
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, infOffset));
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
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DATA
    {
        public uint cbSize; public uint DriverType; public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ManufacturerName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
        public long DriverDate; public ulong DriverVersion;
    }

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiEnumDriverInfo(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType, uint memberIndex, ref SP_DRVINFO_DATA driverInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDriverInfoDetail(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData, IntPtr driverInfoDetailData, uint driverInfoDetailDataSize, out uint requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiSetSelectedDriver(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private void ConfigureUsbDevice(PnpDevice phone)
    {
        using var baseKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{phone.Id}", writable: true) ?? throw new InvalidOperationException("Cannot open the Apple USB PnP registry key.");
        var drv = baseKey.GetValue("Driver") as string;
        if (string.IsNullOrWhiteSpace(drv)) throw new InvalidOperationException("Apple USB device has no usbccgp driver key.");
        using var sw = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{drv}", writable: true) ?? throw new InvalidOperationException("Cannot open the usbccgp software key.");
        sw.SetValue("EnumeratorClass", new byte[] { 0x02, 0x00, 0x00 }, RegistryValueKind.Binary);
        var lower = baseKey.GetValue("LowerFilters") as string[];
        if (lower is not null && lower.Contains("AppleLowerFilter", StringComparer.OrdinalIgnoreCase))
        {
            var remaining = lower.Where(x => !x.Equals("AppleLowerFilter", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == 0) baseKey.DeleteValue("LowerFilters", false); else baseKey.SetValue("LowerFilters", remaining, RegistryValueKind.MultiString);
        }
        SetConfig(phone.Id, SafeIndexValue, "0"); RestartDevice(phone.Id);
    }

    private static void SetConfig(string pnpId, string original, string alt)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnpId}\Device Parameters", writable: true) ?? throw new InvalidOperationException("Cannot open Apple USB device parameters.");
        k.SetValue("OriginalConfigurationValue", uint.Parse(original), RegistryValueKind.DWord); k.SetValue("AltConfigurationValue", uint.Parse(alt), RegistryValueKind.DWord);
    }

    private static void RestartDevice(string id)
    {
        var result = RunAllowRestart("pnputil.exe", $"/restart-device \"{id}\"");
        if (result.ExitCode != 0 && result.ExitCode != 3010) throw new InvalidOperationException($"PNPUTIL.exe failed ({result.ExitCode}): {result.Error}");
    }

    private static void DisablePhotoInterfaces()
    {
        foreach (var d in FindPnP("VID_05AC&PID_", "WPD"))
        {
            if (!d.Id.Contains("&MI_00\\", StringComparison.OrdinalIgnoreCase)) continue;
            var r = RunAllowRestart("pnputil.exe", $"/disable-device \"{d.Id}\"");
            if (r.ExitCode != 0 && r.ExitCode != 3010) throw new InvalidOperationException($"PNPUTIL.exe failed ({r.ExitCode}): {r.Error}");
        }
    }

    private static CommandResult RunAllowRestart(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit(); return new CommandResult(p.ExitCode, output, error);
    }

    private async Task ConfigureIcsAsync(string wifi, string phoneAdapter)
    {
        WriteLog($"Configuring Internet Connection Sharing: {wifi} -> {phoneAdapter}"); DisableAllIcs();
        var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare") ?? throw new InvalidOperationException("Windows Internet Connection Sharing is unavailable.");
        dynamic mgr = Activator.CreateInstance(mgrType)!; dynamic? wifiCfg = null, phoneCfg = null;
        foreach (var c in mgr.EnumEveryConnection()) { dynamic props = mgr.NetConnectionProps(c); if ((string)props.Name == wifi) wifiCfg = mgr.INetSharingConfigurationForINetConnection(c); if ((string)props.Name == phoneAdapter) phoneCfg = mgr.INetSharingConfigurationForINetConnection(c); }
        if (wifiCfg is null || phoneCfg is null) throw new InvalidOperationException("Windows ICS did not expose the Wi-Fi and USB Ethernet adapters.");
        wifiCfg.EnableSharing(0); phoneCfg.EnableSharing(1); await Task.Delay(3000); WriteLog("Internet Connection Sharing enabled.");
    }

    private static void DisableAllIcs()
    {
        try { var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare"); if (mgrType is null) return; dynamic mgr = Activator.CreateInstance(mgrType)!; foreach (var c in mgr.EnumEveryConnection()) { dynamic cfg = mgr.INetSharingConfigurationForINetConnection(c); if ((bool)cfg.SharingEnabled) cfg.DisableSharing(); } } catch { }
    }

    private static bool IsIcsEnabled(string name)
    {
        try { var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare"); if (mgrType is null) return false; dynamic mgr = Activator.CreateInstance(mgrType)!; foreach (var c in mgr.EnumEveryConnection()) { dynamic props = mgr.NetConnectionProps(c); if ((string)props.Name != name) continue; dynamic cfg = mgr.INetSharingConfigurationForINetConnection(c); return (bool)cfg.SharingEnabled; } } catch { }
        return false;
    }

    private static string? FindLease(string adapterName)
    {
        try { var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapterName); if (nic is null) return null; foreach (var ua in nic.GetIPProperties().UnicastAddresses) if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ua.Address.ToString().StartsWith(Subnet, StringComparison.Ordinal)) return ua.Address.ToString(); } catch { }
        return null;
    }

    private static (double Rx, double Tx) GetRates(string adapterName)
    {
        try { var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapterName); return nic is null ? (0, 0) : (nic.GetIPv4Statistics().BytesReceived, nic.GetIPv4Statistics().BytesSent); } catch { return (0, 0); }
    }

    private static NetworkInterface? FindWifi() => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up);

    private static NetworkInterface? FindPhoneAdapter() => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && (n.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Apple", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("NCM", StringComparison.OrdinalIgnoreCase)) && n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211);

    private static IEnumerable<PnpDevice> FindPnP(string hardwareContains, string? className)
    {
        using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID, Name, PNPClass FROM Win32_PnPEntity");
        foreach (ManagementObject o in searcher.Get())
        {
            var id = o["PNPDeviceID"]?.ToString() ?? "";
            var name = o["Name"]?.ToString() ?? "";
            var cls = o["PNPClass"]?.ToString() ?? "";
            if (id.Contains(hardwareContains, StringComparison.OrdinalIgnoreCase) && (className is null || cls.Equals(className, StringComparison.OrdinalIgnoreCase))) yield return new PnpDevice(id, name);
        }
    }

    private static PnpDevice? FindAppleDevice() => FindPnP("USB\\VID_05AC&PID_", null)
        .Where(d => !d.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault();

    private static string GetApplePid(string id)
    {
        var marker = "PID_";
        var start = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) throw new InvalidOperationException($"Unable to determine Apple USB PID from PnP ID: {id}");
        start += marker.Length;
        var end = id.IndexOf('&', start);
        return (end < 0 ? id[start..] : id[start..end]).Trim();
    }

    private static void LogAppleInterfaces()
    {
        foreach (var d in FindPnP("USB\\VID_05AC&PID_", null))
            WriteStaticLog($"Apple USB PnP node: {d.Id} | {d.Name}");
    }

    private static bool TryGetInterfaceNumber(string id, out int number)
    {
        number = -1;
        var marker = "&MI_";
        var start = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || start + marker.Length + 2 > id.Length) return false;
        return int.TryParse(id.Substring(start + marker.Length, 2), System.Globalization.NumberStyles.HexNumber, null, out number);
    }

    private static async Task WaitUntil(Func<bool> predicate, int seconds, string what)
    {
        for (var i = 0; i < seconds; i++) { if (predicate()) return; await Task.Delay(1000); }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public readonly record struct Status(bool AppleConnected, string AppleName, string? AdapterName, string AdapterStatus, bool Sharing, string? Lease, double Rx, double Tx);
    private readonly record struct CommandResult(int ExitCode, string Output, string Error);
    private sealed record PnpDevice(string Id, string Name);
}
