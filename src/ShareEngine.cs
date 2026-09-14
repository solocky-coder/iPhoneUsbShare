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
            // Re-apply the UsbCcgp CDC enumeration policy on every start.
            // Older installations can have a stale usbccgp software key from
            // before NCM support was enabled; in that case Windows creates
            // plain MI_XX child PDOs and UsbNcm.inf has no compatible ID to match.
            // Microsoft documents EnumeratorClass=02,00,00 as the setting that
            // makes usbccgp enumerate CDC interface collections by their class.
            ConfigureUsbCgpEnumerator(phone.Id);
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

            // The iPad exposes two CDC-NCM pairs in configuration 5.
            // Identify the functions from their live descriptors, map them to
            // Windows child PDOs, and let Windows select the compatible NCM
            // driver. Do not assume MI_02/MI_04 or a particular Apple PID.
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
                ConfigureUsbCgpEnumerator(current.Id);
                if (retryMode == "5:3:3:0" || retryMode == "5:3:3")
                {
                    WriteLog("Retry device is already in CDC-NCM direct mode (5); binding NCM without another SET_MODE.");
                }
                else
                {
                    if (retryMode != "3:3:3:0" && retryMode != "3:3:3") throw new InvalidOperationException("Apple USB device did not return to a usable safe/NCM transition mode for the retry.");
                    SetConfig(current.Id, NcmIndexValue, SafeIndexValue);
                    if (!await UsbNative.SetModeAsync(3)) throw new InvalidOperationException("Apple device rejected the CDC-NCM retry mode switch.");
                }
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

        // EnumeratorClass is read by usbccgp when it enumerates the composite
        // device.  Merely changing the registry value does not guarantee that
        // the already-running composite devnode rebuilds its child PDOs.  The
        // previous run proved this: the descriptors contained NCM interfaces
        // 2 and 4, but Windows exposed only MI_00/01/03/05.  Force a targeted
        // ConfigMgr re-enumeration of this exact Apple composite devnode before
        // giving up.  This is a devnode/PnP operation only; it does not change
        // the Apple USB mode or configuration.
        if (targets.Count == 0)
        {
            WriteLog("NCM child PDOs are missing; forcing targeted usbccgp devnode re-enumeration before driver replacement.");
            if (ReenumerateAppleCompositeDevNode(out var reenumError))
            {
                await Task.Delay(1500);
                appleChildren = FindPnP("USB\\VID_05AC&PID_", null).ToList();
                targets = controlInterfaces
                    .Select(n => appleChildren.FirstOrDefault(d => TryGetInterfaceNumber(d.Id, out var mi) && mi == n))
                    .Where(d => d is not null)
                    .Cast<PnpDevice>()
                    .ToList();
                WriteLog($"usbccgp targeted re-enumeration completed; NCM child targets now: {targets.Count}.");
            }
            else
            {
                WriteLog($"usbccgp targeted re-enumeration failed, ConfigMgr error={reenumError}.");
            }
        }

        foreach (var target in targets)
        {
            LogPnpDriverState(target.Id, "NCM candidate");
            LogPnpIds(target.Id);
            LogPnpUtilDrivers(target.Id);
        }

        if (targets.Count == 0)
        {
            WriteLog("CDC-NCM descriptors were present, but Windows still exposed no matching Apple MI child nodes after targeted usbccgp re-enumeration.");
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
                "root\\CIMV2",
                "SELECT PNPDeviceID, Name, Service, DriverVersion, Manufacturer, ConfigManagerErrorCode, Status, PNPClass FROM Win32_PnPEntity");
            foreach (ManagementObject o in searcher.Get())
            {
                var id = o["PNPDeviceID"]?.ToString() ?? "";
                if (!id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                WriteStaticLog($"{prefix}: id={id} | name={o["Name"]} | class={o["PNPClass"]} | service={o["Service"]} | driver={o["DriverVersion"]} | manufacturer={o["Manufacturer"]} | configError={o["ConfigManagerErrorCode"]} | status={o["Status"]}");
                return;
            }
            WriteStaticLog($"{prefix}: no Win32_PnPEntity row found");
        }
        catch (Exception ex) { WriteStaticLog($"{prefix}: driver-state query failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void LogPnpIds(string instanceId)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            if (baseKey is null) { WriteStaticLog($"PnP IDs {instanceId}: registry key not found"); return; }
            var hw = baseKey.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
            var compat = baseKey.GetValue("CompatibleIDs") as string[] ?? Array.Empty<string>();
            var service = baseKey.GetValue("Service")?.ToString() ?? "";
            var driver = baseKey.GetValue("Driver")?.ToString() ?? "";
            WriteStaticLog($"PnP IDs {instanceId}: HardwareID=[{string.Join(" | ", hw)}] | CompatibleIDs=[{string.Join(" | ", compat)}] | Service={service} | DriverKey={driver}");
        }
        catch (Exception ex) { WriteStaticLog($"PnP IDs {instanceId}: query failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void LogPnpUtilDrivers(string instanceId)
    {
        try
        {
            var r = RunAllowRestart("pnputil.exe", $"/enum-devices /instanceid \"{instanceId}\" /drivers");
            WriteStaticLog($"pnputil driver enumeration {instanceId}: exit={r.ExitCode}");
            if (!string.IsNullOrWhiteSpace(r.Output)) WriteStaticLog($"pnputil driver enumeration output: {r.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(r.Error)) WriteStaticLog($"pnputil driver enumeration error: {r.Error.Trim()}");
        }
        catch (Exception ex) { WriteStaticLog($"pnputil driver enumeration failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void WriteStaticLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try { lock (LogFileLock) File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ActivityLog.txt"), line + Environment.NewLine, new UTF8Encoding(false)); } catch { }
    }

    private bool InstallSelectedNcmDriver(string instanceId, string infPath, out uint error)
    {
        // The previous implementation searched the normal class-driver list.
        // That list is rank-filtered, so the installed Apple Netaapl driver
        // won over Microsoft's generic CDC-NCM match.  Build a driver list
        // from *only* the Microsoft INF for this exact devnode, then install
        // the selected driver on that devnode.  This is a targeted replacement:
        // it does not uninstall Apple's package globally and does not alter
        // unrelated Apple devices.
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

                var installParams = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>(), DriverPath = string.Empty };
                if (!SetupDiGetDeviceInstallParams(h, ref devInfo, ref installParams))
                {
                    error = (uint)Marshal.GetLastWin32Error();
                    WriteStaticLog($"SetupAPI: SetupDiGetDeviceInstallParams failed for {instanceId}, Win32Error={error}");
                    return false;
                }

                installParams.Flags |= DI_ENUMSINGLEINF | DI_QUIETINSTALL;
                installParams.FlagsEx |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS | DI_FLAGSEX_FILTERSIMILARDRIVERS;
                installParams.DriverPath = infPath;

                if (!SetupDiSetDeviceInstallParams(h, ref devInfo, ref installParams))
                {
                    error = (uint)Marshal.GetLastWin32Error();
                    WriteStaticLog($"SetupAPI: SetupDiSetDeviceInstallParams failed for {instanceId}, Win32Error={error}");
                    return false;
                }

                WriteStaticLog($"SetupAPI: building driver list restricted to {infPath} for {instanceId}");
                if (!SetupDiBuildDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER))
                {
                    error = (uint)Marshal.GetLastWin32Error();
                    WriteStaticLog($"SetupAPI: restricted driver-list build failed for {instanceId}, Win32Error={error}");
                    return false;
                }

                try
                {
                    var found = false;
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

                        var detail = GetDriverInfoDetail(h, ref devInfo, ref driver, out var detailError);
                        WriteStaticLog($"SetupAPI restricted candidate for {instanceId}: inf={detail ?? "<unknown>"} | description={driver.Description} | provider={driver.ProviderName} | detailError={detailError}");

                        if (detail is null || !string.Equals(Path.GetFileName(detail), Path.GetFileName(infPath), StringComparison.OrdinalIgnoreCase))
                            continue;

                        found = true;
                        WriteStaticLog($"SetupAPI: selecting exact Microsoft NCM driver node {Path.GetFileName(detail)} for {instanceId}");
                        if (!SetupDiSetSelectedDriver(h, ref devInfo, ref driver))
                        {
                            error = (uint)Marshal.GetLastWin32Error();
                            WriteStaticLog($"SetupAPI: SetupDiSetSelectedDriver failed for {instanceId}, Win32Error={error}");
                            return false;
                        }

                        // DiInstallDevice is the supported API for installing a
                        // preinstalled driver node on a specific present device.
                        // Unlike the previous DIF_SELECTBESTCOMPATDRV path, it
                        // does not ask Windows to re-rank the Apple hardware-ID
                        // driver after we have explicitly selected UsbNcm.
                        if (!DiInstallDevice(IntPtr.Zero, h, ref devInfo, ref driver, 0, out var needReboot))
                        {
                            error = (uint)Marshal.GetLastWin32Error();
                            WriteStaticLog($"SetupAPI: DiInstallDevice(UsbNcm) failed for {instanceId}, Win32Error={error}, needReboot={needReboot}");
                            return false;
                        }

                        WriteStaticLog($"SetupAPI: Microsoft UsbNcm installed on {instanceId}; needReboot={needReboot}");
                        return true;
                    }

                    if (!found)
                    {
                        // Do not fall back to DIF_SELECTBESTCOMPATDRV here.
                        // That was the operation that allowed Netaapl/oem25 to
                        // remain selected.  Failure is intentionally explicit
                        // so the log tells us whether the INF itself does not
                        // declare compatibility with this devnode.
                        error = ERROR_NO_MORE_ITEMS;
                        WriteStaticLog($"SetupAPI: {Path.GetFileName(infPath)} was not exposed as a compatible driver when the search was restricted to that INF for {instanceId}");
                        return false;
                    }
                }
                finally
                {
                    SetupDiDestroyDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER);
                }
            }

            error = ERROR_NO_SUCH_DEVINST;
            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(h);
        }
    }

    private static string? GetDeviceInstanceId(IntPtr h, ref SP_DEVINFO_DATA devInfo)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(h, ref devInfo, buffer, buffer.Capacity, out _) ? buffer.ToString() : null;
    }

    private static string? GetDriverInfoDetail(IntPtr h, ref SP_DEVINFO_DATA devInfo, ref SP_DRVINFO_DATA driver, out int error)
    {
        error = 0;
        // SP_DRVINFO_DETAIL_DATA_W has a fixed header followed by variable-length
        // hardware/compatible IDs.  The previous implementation used an incorrect
        // x64 size and INF-file offset, which caused ERROR_INVALID_USER_BUFFER (1784)
        // and made SetupAPI enumerate unrelated NICs instead of usbncm.inf.
        var baseSize = Marshal.SizeOf<SP_DRVINFO_DETAIL_DATA>();
        uint requiredSize = 0;
        SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, IntPtr.Zero, 0, out requiredSize);
        var firstError = Marshal.GetLastWin32Error();
        if (requiredSize < (uint)baseSize)
        {
            error = firstError;
            return null;
        }

        var bufferSize = checked((int)Math.Max(requiredSize, (uint)baseSize));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // The API requires cbSize to be the size of the fixed structure header,
            // not the total allocation including the trailing variable IDs.
            Marshal.WriteInt32(buffer, baseSize);
            if (!SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, buffer, (uint)bufferSize, out requiredSize))
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }

            var detail = Marshal.PtrToStructure<SP_DRVINFO_DETAIL_DATA>(buffer);
            return detail.InfFileName;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint SPDIT_CLASSDRIVER = 0x00000001;
    private const uint DIF_INSTALLDEVICE = 0x00000001;
    private const uint DI_ENUMSINGLEINF = 0x00000002;
    private const uint DI_QUIETINSTALL = 0x00000020;
    private const uint DI_FLAGSEX_ALLOWEXCLUDEDDRVS = 0x00000001;
    private const uint DI_FLAGSEX_FILTERSIMILARDRIVERS = 0x00000200;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_NO_SUCH_DEVINST = 433;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINSTALL_PARAMS
    {
        public uint cbSize;
        public uint Flags;
        public uint FlagsEx;
        public IntPtr hwndParent;
        public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext;
        public IntPtr FileQueue;
        public IntPtr ClassInstallReserved;
        public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DETAIL_DATA
    {
        public uint cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string InfFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SectionName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DrvDescription;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1)] public string HardwareID;
    }

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
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DEVINSTALL_PARAMS deviceInstallParams);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiSetDeviceInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DEVINSTALL_PARAMS deviceInstallParams);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiEnumDriverInfo(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType, uint memberIndex, ref SP_DRVINFO_DATA driverInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDriverInfoDetail(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData, IntPtr driverInfoDetailData, uint driverInfoDetailDataSize, out uint requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiSetSelectedDriver(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool DiInstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData, uint flags, out bool needReboot);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private const uint CR_SUCCESS = 0x00000000;
    private const uint CM_REENUMERATE_NORMAL = 0x00000000;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    private static bool ReenumerateAppleCompositeDevNode(out uint error)
    {
        error = 0;
        try
        {
            var phone = FindAppleDevice();
            if (phone is null)
            {
                error = 1;
                WriteStaticLog("ConfigMgr re-enumeration: Apple composite devnode not found.");
                return false;
            }

            var cr = CM_Locate_DevNodeW(out var devInst, phone.Id, 0);
            if (cr != CR_SUCCESS)
            {
                error = cr;
                WriteStaticLog($"ConfigMgr: CM_Locate_DevNode failed for {phone.Id}, CR=0x{cr:X8}");
                return false;
            }

            cr = CM_Reenumerate_DevNode(devInst, CM_REENUMERATE_NORMAL);
            error = cr;
            if (cr != CR_SUCCESS)
            {
                WriteStaticLog($"ConfigMgr: CM_Reenumerate_DevNode failed for {phone.Id}, CR=0x{cr:X8}");
                return false;
            }

            WriteStaticLog($"ConfigMgr: re-enumerated Apple composite devnode {phone.Id} successfully.");
            return true;
        }
        catch (Exception ex)
        {
            error = 1;
            WriteStaticLog($"ConfigMgr re-enumeration failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void ConfigureUsbCgpEnumerator(string pnpId)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnpId}", writable: true);
            var drv = baseKey?.GetValue("Driver") as string;
            if (string.IsNullOrWhiteSpace(drv)) throw new InvalidOperationException("Apple USB device has no usbccgp driver key.");
            using var sw = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{drv}", writable: true);
            if (sw is null) throw new InvalidOperationException($"Cannot open usbccgp software key {drv}.");
            sw.SetValue("EnumeratorClass", new byte[] { 0x02, 0x00, 0x00 }, RegistryValueKind.Binary);
            var lower = baseKey!.GetValue("LowerFilters") as string[];
            if (lower is not null && lower.Contains("AppleLowerFilter", StringComparer.OrdinalIgnoreCase))
            {
                var remaining = lower.Where(x => !x.Equals("AppleLowerFilter", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (remaining.Length == 0) baseKey.DeleteValue("LowerFilters", false);
                else baseKey.SetValue("LowerFilters", remaining, RegistryValueKind.MultiString);
                WriteStaticLog($"Removed AppleLowerFilter from {pnpId}.");
            }
            WriteStaticLog($"usbccgp EnumeratorClass set to 02 00 00 on {drv}; re-enumeration will regenerate CDC compatible IDs.");
        }
        catch (Exception ex) { WriteStaticLog($"usbccgp EnumeratorClass update failed: {ex.GetType().Name}: {ex.Message}"); throw; }
    }

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
