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
        // EXPERIMENT: test whether Microsoft's USB NCM compatible ID is the
        // missing PnP match on Windows 10.  We deliberately do NOT install a
        // custom/unsigned INF and do NOT alter the Apple USB mode here.
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

        if (targets.Count == 0)
        {
            WriteLog("NCM child PDOs are missing; forcing targeted usbccgp devnode re-enumeration.");
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

        if (targets.Count == 0)
        {
            WriteLog("CDC-NCM descriptors were present, but Windows exposed no matching Apple MI child nodes.");
            LogAppleInterfaces();
            return;
        }

        var target = targets[0]; // first NCM function has the interrupt endpoint and is tethering.
        LogPnpDriverState(target.Id, "Before MS_COMP_WINNCM experiment");
        LogPnpIds(target.Id);
        LogPnpUtilDrivers(target.Id);

        var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "usbncm.inf");
        WriteLog($"Microsoft UsbNcm INF present: {File.Exists(inf)} ({inf})");
        if (!File.Exists(inf))
        {
            WriteLog("usbncm.inf is not present; cannot perform MS_COMP_WINNCM experiment.");
            return;
        }

        // TEST ONLY: PnP normally obtains CompatibleIDs from the bus driver.
        // Add the Microsoft NCM compatible ID to this already-enumerated
        // devnode and ask ConfigMgr to re-enumerate it.  We preserve every
        // existing CompatibleIDs entry and log the before/after values.
        if (!InjectMicrosoftNcmCompatibleId(target.Id, out var injectError))
        {
            WriteLog($"MS_COMP_WINNCM injection failed: {injectError}");
            WriteLog("No custom INF was generated or installed.");
            return;
        }

        WriteLog("MS_COMP_WINNCM injected successfully. Re-enumerating the target NCM devnode.");
        if (!ReenumerateDevNode(target.Id, out var devError))
            WriteLog($"Target NCM devnode re-enumeration failed: ConfigMgr error={devError}");

        await Task.Delay(2500);
        LogPnpDriverState(target.Id, "After MS_COMP_WINNCM experiment");
        LogPnpIds(target.Id);
        LogPnpUtilDrivers(target.Id);

        var adapter = FindPhoneAdapter();
        if (adapter?.OperationalStatus == OperationalStatus.Up)
        {
            WriteLog($"SUCCESS: MS_COMP_WINNCM experiment produced a usable USB Ethernet adapter: {adapter.Name}");
            return;
        }

        WriteLog("MS_COMP_WINNCM experiment did not produce an active USB Ethernet adapter.");
        WriteLog("This is a diagnostic result; no unsigned companion INF was installed.");
    }

    private static bool InjectMicrosoftNcmCompatibleId(string instanceId, out string error)
    {
        error = "";
        const string id = "USB\\MS_COMP_WINNCM";
        try
        {
            var subKeyPath = $@"SYSTEM\CurrentControlSet\Enum\{instanceId}";
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true);
            if (key is null)
            {
                error = $"PnP registry key not accessible: HKLM\\{subKeyPath}";
                return false;
            }

            var before = key.GetValue("CompatibleIDs") as string[] ?? Array.Empty<string>();
            WriteStaticLog($"MS_COMP_WINNCM experiment BEFORE: CompatibleIDs=[{string.Join(" | ", before)}]");

            if (!before.Any(x => x.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                var after = before.Concat(new[] { id }).ToArray();
                key.SetValue("CompatibleIDs", after, RegistryValueKind.MultiString);
                WriteStaticLog($"MS_COMP_WINNCM experiment AFTER: CompatibleIDs=[{string.Join(" | ", after)}]");
            }
            else
            {
                WriteStaticLog("MS_COMP_WINNCM experiment: compatible ID already present.");
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private const uint CR_SUCCESS = 0x00000000;
    private const uint CM_REENUMERATE_NORMAL = 0x00000000;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    private static bool ReenumerateAppleCompositeDevNode(out uint error)
    {
        var phone = FindAppleDevice();
        if (phone is null)
        {
            error = 1;
            WriteStaticLog("ConfigMgr re-enumeration: Apple composite devnode not found.");
            return false;
        }
        return ReenumerateDevNode(phone.Id, out error);
    }

    private static bool ReenumerateDevNode(string instanceId, out uint error)
    {
        error = 0;
        try
        {
            var cr = CM_Locate_DevNodeW(out var devInst, instanceId, 0);
            if (cr != CR_SUCCESS)
            {
                error = cr;
                WriteStaticLog($"ConfigMgr: CM_Locate_DevNode failed for {instanceId}, CR=0x{cr:X8}");
                return false;
            }

            cr = CM_Reenumerate_DevNode(devInst, CM_REENUMERATE_NORMAL);
            error = cr;
            if (cr != CR_SUCCESS)
            {
                WriteStaticLog($"ConfigMgr: CM_Reenumerate_DevNode failed for {instanceId}, CR=0x{cr:X8}");
                return false;
            }

            WriteStaticLog($"ConfigMgr: re-enumerated devnode {instanceId} successfully.");
            return true;
        }
        catch (Exception ex)
        {
            error = 1;
            WriteStaticLog($"ConfigMgr devnode re-enumeration failed: {ex.GetType().Name}: {ex.Message}");
            return false;
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


