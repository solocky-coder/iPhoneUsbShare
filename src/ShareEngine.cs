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

        // Do not try to out-rank Apple's driver through SetupAPI.  Windows' inbox
        // UsbNcm INF normally matches USB\MS_COMP_WINNCM / CDC-NCM class IDs, while
        // Apple's composite child also has a more-specific VID/PID/MI hardware ID.
        // Install a tiny device-specific companion INF which maps the actual Apple
        // NCM control-interface hardware ID to Microsoft's existing UsbNcm install
        // sections.  The companion package does not contain or replace UsbNcm.sys.
        var companionTargets = targets.Take(1).ToList(); // first NCM function is the tethering function; later one is RemoteXPC
        var companionInf = CreateNcmCompanionInf(companionTargets, out var companionError);
        if (companionInf is null)
        {
            WriteLog($"NCM companion INF creation failed: {companionError}");
            return;
        }

        WriteLog($"NCM companion INF: {companionInf}");
        var add = RunAllowRestart("pnputil.exe", $"/add-driver \"{companionInf}\" /install");
        WriteLog($"NCM companion INF registration exit code: {add.ExitCode}");
        if (!string.IsNullOrWhiteSpace(add.Output)) WriteLog($"NCM companion INF output: {add.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(add.Error)) WriteLog($"NCM companion INF error: {add.Error.Trim()}");

        if (add.ExitCode != 0)
        {
            WriteLog("NCM companion INF was not accepted by PnP; no SetupAPI driver-ranking fallback will be attempted.");
            return;
        }

        // Give PnP a moment to apply the newly introduced exact hardware-ID match,
        // then restart only the targeted Apple NCM devnode.  No USB mode change is
        // performed here and Apple's global driver package is left installed.
        foreach (var target in companionTargets)
        {
            WriteLog($"Restarting Apple NCM devnode after companion-INF install: {target.Id}");
            try { RestartDevice(target.Id); } catch (Exception ex) { WriteLog($"NCM companion devnode restart: {ex.Message}"); }
            await Task.Delay(2500);
            LogPnpDriverState(target.Id, "after NCM companion INF");
            LogPnpIds(target.Id);
            LogPnpUtilDrivers(target.Id);

            var adapter = FindPhoneAdapter();
            if (adapter?.OperationalStatus == OperationalStatus.Up)
            {
                WriteLog($"NCM companion INF produced a usable adapter: {adapter.Name}");
                return;
            }
        }

        WriteLog("NCM companion INF installed, but no active USB Ethernet adapter is visible yet.");
    }

    private string? CreateNcmCompanionInf(IReadOnlyList<PnpDevice> targets, out string error)
    {
        error = "";
        if (targets.Count == 0)
        {
            error = "no NCM target devnode was supplied";
            return null;
        }

        var hardwareId = targets[0].Id;
        if (!hardwareId.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) ||
            hardwareId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0)
        {
            error = $"unexpected Apple NCM instance ID: {hardwareId}";
            return null;
        }

        try
        {
            var dir = Path.Combine(CacheDir, "NcmCompanion");
            Directory.CreateDirectory(dir);
            var safeName = hardwareId.Replace('\\', '_').Replace('&', '_').Replace(':', '_');
            var infPath = Path.Combine(dir, $"iPhoneUsbShareNcm_{safeName}.inf");

            // This is deliberately an INF-only wrapper.  UsbNcm.sys and its Microsoft
            // catalog remain owned by Windows; the wrapper simply supplies an exact
            // Apple VID/PID/MI match and delegates installation to the inbox sections.
            var inf = $"""
; iPhoneUsbShare Apple USB NCM companion INF
; Generated for the currently enumerated Apple NCM tethering interface.
; This package does not contain UsbNcm.sys; it delegates to Microsoft's inbox INF.

[Version]
Signature="$WINDOWS NT$"
Class=Net
ClassGuid={{4d36e972-e325-11ce-bfc1-08002be10318}}
Provider=%ProviderName%
DriverVer=09/14/2026,1.0.0.0
PnpLockdown=1

[Manufacturer]
%ManufacturerName%=DeviceList,NTamd64

[DeviceList.NTamd64]
%DeviceDesc%=UsbNcm_Device, {hardwareId}

[UsbNcm_Device.NT]
Include=usbncm.inf
Needs=UsbNcm_Device.NT

[UsbNcm_Device.NT.Services]
Include=usbncm.inf
Needs=UsbNcm_Device.NT.Services

[Strings]
ProviderName="iPhoneUsbShare"
ManufacturerName="iPhoneUsbShare"
DeviceDesc="Apple USB NCM (Microsoft UsbNcm)"
""";

            File.WriteAllText(infPath, inf, new UTF8Encoding(false));
            WriteLog($"Generated Apple NCM companion INF for exact hardware ID: {hardwareId}");
            WriteLog($"NCM companion INF contents: Include=usbncm.inf; Needs=UsbNcm_Device.NT / UsbNcm_Device.NT.Services");
            return infPath;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
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
