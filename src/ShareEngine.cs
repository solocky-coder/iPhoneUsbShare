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

namespace iPhoneUsbShare;

public sealed class ShareEngine
{
    public event EventHandler<string>? Log;
    private const string Vendor = "05AC";
    private const string IphonePid = "12A8";
    private const string IpadPid = "12AB";
    private const string SafeIndexValue = "2";
    private const string NcmIndexValue = "4";
    private const string Subnet = "192.168.137.";
    private const string LibUsbZipUrl = "https://github.com/mcuee/libusb-win32/releases/download/release_1.4.0.2/libusb-win32-bin-1.4.0.2.zip";
    private const string LibUsbZipSha256 = "00004c92cdb99be36e17fb2377165eb97e63b48ba895bfc04a642ea9c3e26d94";
    private string AppDir => AppContext.BaseDirectory;
    private string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iPhoneUsbShare");

    public async Task EnsurePrerequisitesAsync()
    {
        Directory.CreateDirectory(CacheDir);
        if (UsbNative.IsReachable()) return;
        Log?.Invoke(this, "Installing the USB filter driver (one-time)…");
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
        Run(installer, $"install --device=USB\\VID_{Vendor}&PID_{GetApplePid(phone.Id)}");
        var sysDst = Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys");
        if (!File.Exists(sysDst)) File.Copy(sys, sysDst, false);
        try
        {
            using var svc = new System.ServiceProcess.ServiceController("libusb0");
            if (svc.Status != System.ServiceProcess.ServiceControllerStatus.Running) svc.Start();
        }
        catch { }
        ConfigureUsbDevice(phone);
        Log?.Invoke(this, "USB driver setup complete.");
        await WaitUntil(() => UsbNative.IsReachable(), 15, "Apple USB filter driver");
    }

    public async Task StartAsync()
    {
        await EnsurePrerequisitesAsync();
        var phone = FindAppleDevice() ?? throw new InvalidOperationException("iPhone/iPad not found.");
        DisablePhotoInterfaces();
        var wifi = FindWifi() ?? throw new InvalidOperationException("No connected Wi-Fi adapter found.");
        Log?.Invoke(this, $"Internet source: {wifi.Name}");
        var adapter = FindPhoneAdapter();
        if (adapter is not null && adapter.OperationalStatus == OperationalStatus.Up) Log?.Invoke(this, $"USB Ethernet already available: {adapter.Name}");
        else
        {
            SetConfig(phone.Id, SafeIndexValue, "0");
            RestartDevice(phone.Id);
            await WaitUntil(() => FindAppleDevice() is not null, 25, "Apple device to re-enumerate");
            DisablePhotoInterfaces();
            Log?.Invoke(this, "Waiting for Apple USB control interface…");
            string? mode = null;
            for (var i = 0; i < 20; i++)
            {
                mode = await UsbNative.GetModeAsync();
                if (mode is not null) break;
                await Task.Delay(1000);
            }
            if (mode is null)
                throw new InvalidOperationException("Apple USB control interface is unreachable after the device restart. The libusb filter did not reattach; unplug/replug the iPad and try again.");
            Log?.Invoke(this, $"Apple USB mode: {mode}");
            if (mode != "3:3:3:0") throw new InvalidOperationException($"Unexpected Apple USB mode: {mode}.");
            SetConfig(phone.Id, NcmIndexValue, SafeIndexValue);
            var accepted = await UsbNative.SetModeAsync(3);
            if (!accepted)
            {
                SetConfig(phone.Id, SafeIndexValue, "0");
                throw new InvalidOperationException("The Apple device rejected the CDC-NCM mode switch. Unplug/replug and try again.");
            }
            Log?.Invoke(this, "Apple device accepted CDC-NCM mode; waiting for USB Ethernet…");
            await WaitUntil(() => FindPhoneAdapter()?.OperationalStatus == OperationalStatus.Up, 35, "USB Ethernet adapter");
            adapter = FindPhoneAdapter() ?? throw new InvalidOperationException("USB Ethernet adapter did not start.");
            DisablePhotoInterfaces();
        }
        await ConfigureIcsAsync(wifi.Name, adapter.Name);
        Log?.Invoke(this, "Waiting for DHCP lease…");
        await WaitUntil(() => FindLease(adapter.Name) is not null, 30, "phone DHCP lease");
    }

    public Task StopAsync()
    {
        try { var adapter = FindPhoneAdapter(); if (adapter is not null) DisableAllIcs(); var phone = FindAppleDevice(); if (phone is not null) SetConfig(phone.Id, SafeIndexValue, "0"); } catch { }
        return Task.CompletedTask;
    }

    public async Task<Status> GetStatusAsync()
    {
        var p = FindAppleDevice(); var a = FindPhoneAdapter(); var lease = a is null ? null : FindLease(a.Name); var sharing = a is not null && IsIcsEnabled(a.Name); var (rx, tx) = a is null ? (0d, 0d) : GetRates(a.Name);
        return await Task.FromResult(new Status(p is not null, p?.Name ?? "Apple device", a?.Name, a?.OperationalStatus.ToString() ?? "—", sharing, lease, rx, tx));
    }

    public async Task<string> DiagnosticsAsync()
    {
        var sb = new StringBuilder(); var p = FindAppleDevice(); var a = FindPhoneAdapter();
        sb.AppendLine($"Apple device: {(p is null ? "not connected" : p.Name)}"); sb.AppendLine($"Apple PnP ID: {p?.Id ?? "—"}"); sb.AppendLine($"USB identity: {UsbNative.GetDeviceId() ?? "unreachable"}"); sb.AppendLine($"USB mode: {await UsbNative.GetModeAsync() ?? "unreachable"}"); sb.AppendLine($"USB Ethernet: {a?.Name ?? "not present"} [{a?.OperationalStatus.ToString() ?? "—"}]"); sb.AppendLine($"Lease: {(a is null ? "—" : FindLease(a.Name) ?? "none")}"); sb.AppendLine($"Wi-Fi: {FindWifi()?.Name ?? "none"}"); return sb.ToString();
    }

    private void ConfigureUsbDevice(PnpDevice phone)
    {
        using var baseKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{phone.Id}", writable: true) ?? throw new InvalidOperationException("Cannot open the Apple USB PnP registry key.");
        var drv = baseKey.GetValue("Driver") as string; if (string.IsNullOrWhiteSpace(drv)) throw new InvalidOperationException("Apple USB device has no usbccgp driver key.");
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
        foreach (var d in FindPnP("VID_05AC&PID_12A", "WPD")) if (d.Id.Contains("&MI_00\\", StringComparison.OrdinalIgnoreCase))
        {
            var r = RunAllowRestart("pnputil.exe", $"/disable-device \"{d.Id}\"");
            if (r.ExitCode != 0 && r.ExitCode != 3010) throw new InvalidOperationException($"PNPUTIL.exe failed ({r.ExitCode}): {r.Error}");
        }
    }

    private static CommandResult RunAllowRestart(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}."); var output = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit(); return new CommandResult(p.ExitCode, output, error);
    }

    private async Task ConfigureIcsAsync(string wifi, string phoneAdapter)
    {
        DisableAllIcs(); var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare") ?? throw new InvalidOperationException("Windows Internet Connection Sharing is unavailable."); dynamic mgr = Activator.CreateInstance(mgrType)!; dynamic? wifiCfg = null, phoneCfg = null;
        foreach (var c in mgr.EnumEveryConnection()) { dynamic props = mgr.NetConnectionProps(c); if ((string)props.Name == wifi) wifiCfg = mgr.INetSharingConfigurationForINetConnection(c); if ((string)props.Name == phoneAdapter) phoneCfg = mgr.INetSharingConfigurationForINetConnection(c); }
        if (wifiCfg is null || phoneCfg is null) throw new InvalidOperationException("Windows ICS did not expose the Wi-Fi and USB Ethernet adapters."); wifiCfg.EnableSharing(0); phoneCfg.EnableSharing(1); await Task.Delay(3000);
    }

    private static void DisableAllIcs()
    {
        try { var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare"); if (mgrType is null) return; dynamic mgr = Activator.CreateInstance(mgrType)!; foreach (var c in mgr.EnumEveryConnection()) { dynamic cfg = mgr.INetSharingConfigurationForINetConnection(c); if ((bool)cfg.SharingEnabled) cfg.DisableSharing(); } } catch { }
    }

    private static bool IsIcsEnabled(string name)
    {
        try { var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare"); if (mgrType is null) return false; dynamic mgr = Activator.CreateInstance(mgrType)!; foreach (var c in mgr.EnumEveryConnection()) { dynamic props = mgr.NetConnectionProps(c); if ((string)props.Name != name) continue; dynamic cfg = mgr.INetSharingConfigurationForINetConnection(c); return (bool)cfg.SharingEnabled; } } catch { } return false;
    }

    private static string? FindLease(string adapterName)
    {
        try { var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapterName); if (nic is null) return null; foreach (var ua in nic.GetIPProperties().UnicastAddresses) if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ua.Address.ToString().StartsWith(Subnet, StringComparison.Ordinal)) return ua.Address.ToString(); } catch { } return null;
    }
    private static NetworkInterface? FindWifi() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211).FirstOrDefault();
    private static PnpDevice? FindAppleDevice() => FindPnP($"VID_{Vendor}&PID_12A", null).OrderBy(d => d.Id.Contains("&MI_01", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
    private static string GetApplePid(string pnpId) => pnpId.Contains($"PID_{IpadPid}", StringComparison.OrdinalIgnoreCase) ? IpadPid : IphonePid;
    private static NetworkInterface? FindPhoneAdapter() => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id.Contains($"VID_{Vendor}&PID_12A", StringComparison.OrdinalIgnoreCase));
    private static List<PnpDevice> FindPnP(string needle, string? className)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT PNPDeviceID, Name, Status, PNPClass FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%{needle}%'"); var list = new List<PnpDevice>();
        foreach (ManagementObject m in searcher.Get()) { var id = (string?)m["PNPDeviceID"]; var name = (string?)m["Name"]; var status = (string?)m["Status"]; if (id is not null && (className is null || string.Equals(className, (string?)m["PNPClass"], StringComparison.OrdinalIgnoreCase))) list.Add(new PnpDevice(id, name ?? id, status ?? "Unknown")); } return list;
    }
    private static async Task WaitUntil(Func<bool> test, int seconds, string what) { for (var i = 0; i < seconds; i++) { if (test()) return; await Task.Delay(1000); } throw new TimeoutException($"Timed out waiting for {what}."); }
    private static void Run(string file, string args) { var r = RunAllowRestart(file, args); if (r.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} failed ({r.ExitCode}): {r.Error}"); }
    private static (double rx, double tx) GetRates(string name) { var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name); if (nic is null) return (0, 0); var s = nic.GetIPv4Statistics(); return (s.BytesReceived / 1024d, s.BytesSent / 1024d); }
    private sealed record PnpDevice(string Id, string Name, string Status);
    private sealed record CommandResult(int ExitCode, string Output, string Error);
    public sealed record Status(bool PhoneConnected, string PhoneName, string? AdapterName, string AdapterStatus, bool Sharing, string? PhoneIp, double RxKbps, double TxKbps);
}
