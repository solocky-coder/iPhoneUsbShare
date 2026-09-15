using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;

namespace iPhoneUsbShare;

/// <summary>
/// Coordinates the iPhone USB transport and the separate network-provisioning layer.
/// USB/NCM bring-up is deliberately independent from Internet Connection Sharing (ICS).
/// </summary>
public sealed class ShareEngine
{
    public event EventHandler<string>? Log;

    private const string SafeIndexValue = "2";
    private const string NcmIndexValue = "4";
    private const string IcsSubnet = "192.168.137.";
    private static readonly object LogFileLock = new();
    private string AppDir => AppContext.BaseDirectory;
    private string ActivityLogPath => Path.Combine(AppDir, "ActivityLog.txt");
    private string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iPhoneUsbShare");

    public ShareEngine()
    {
        WriteLog("============================================================");
        WriteLog("iPhoneUsbShare transport session started");
        WriteLog($"Application directory: {AppDir}");
    }

    public void WriteLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try { lock (LogFileLock) File.AppendAllText(ActivityLogPath, line + Environment.NewLine, new UTF8Encoding(false)); } catch { }
        Log?.Invoke(this, message);
    }

    public Task EnsurePrerequisitesAsync()
    {
        Directory.CreateDirectory(CacheDir);
        if (!UsbNative.IsReachable())
            throw new InvalidOperationException("Apple USB control interface is not reachable through WinUSB. Verify the iPhoneUsbShare WinUSB driver on MI_00 and reconnect the device.");
        WriteLog("USB control plane: WinUSB reachable; legacy libusb-win32 is not used.");
        return Task.CompletedTask;
    }

    /// <summary>Bring up only USB/NCM. No HNetCfg, DHCP, NAT or Wi-Fi work happens here.</summary>
    public async Task<NetworkInterface> EnsureUsbTransportAsync()
    {
        await EnsurePrerequisitesAsync();
        var phone = FindAppleDevice() ?? throw new InvalidOperationException("iPhone/iPad not found.");
        WriteLog($"USB transport: Apple device {phone.Id} ({phone.Name})");
        DisablePhotoInterfaces();

        var adapter = FindPhoneAdapter();
        if (adapter?.OperationalStatus == OperationalStatus.Up)
        {
            WriteLog($"USB transport: existing NCM adapter is already up: {adapter.Name}");
            return adapter;
        }

        ConfigureUsbCgpEnumerator(phone.Id);
        SetConfig(phone.Id, SafeIndexValue, "0");
        WriteLog("USB transport: armed safe USB configuration (2).");
        RestartDevice(phone.Id);
        await WaitUntil(() => FindAppleDevice() is not null, 25, "Apple USB device to re-enumerate");
        DisablePhotoInterfaces();

        var mode = await WaitForModeAsync();
        if (IsNcmMode(mode))
        {
            WriteLog("USB transport: Apple is already in CDC-NCM mode (5).");
        }
        else
        {
            if (!IsSafeMode(mode)) throw new InvalidOperationException($"Unexpected Apple USB mode: {mode}.");
            SetConfig(phone.Id, NcmIndexValue, SafeIndexValue);
            WriteLog("USB transport: armed CDC-NCM configuration (4).");
            if (!await UsbNative.SetModeAsync(3))
            {
                SetConfig(phone.Id, SafeIndexValue, "0");
                throw new InvalidOperationException("Apple rejected SET_MODE(3).");
            }
            WriteLog("USB transport: Apple accepted CDC-NCM mode switch.");
        }

        await EnsureInboxNcmBindingAsync();
        adapter = await WaitForPhoneAdapterAsync(20);
        if (adapter is not null)
        {
            WriteLog($"USB transport: NCM adapter is up: {adapter.Name}");
            return adapter;
        }

        // iOS can perform a delayed USB reset on a fresh connection. Retry once without
        // involving ICS or any network COM state.
        WriteLog("USB transport: NCM adapter not ready; performing one USB-only recovery pass.");
        phone = FindAppleDevice() ?? throw new InvalidOperationException("Apple device disappeared during USB transport recovery.");
        SetConfig(phone.Id, SafeIndexValue, "0");
        RestartDevice(phone.Id);
        await WaitUntil(() => FindAppleDevice() is not null, 25, "Apple USB device after transport recovery");
        DisablePhotoInterfaces();
        var retryMode = await WaitForModeAsync();
        ConfigureUsbCgpEnumerator(phone.Id);
        if (!IsNcmMode(retryMode))
        {
            if (!IsSafeMode(retryMode)) throw new InvalidOperationException($"Apple USB device did not return to a usable safe/NCM mode: {retryMode}.");
            SetConfig(phone.Id, NcmIndexValue, SafeIndexValue);
            if (!await UsbNative.SetModeAsync(3)) throw new InvalidOperationException("Apple rejected the CDC-NCM recovery mode switch.");
        }
        await EnsureInboxNcmBindingAsync();
        adapter = await WaitForPhoneAdapterAsync(30);
        return adapter ?? throw new InvalidOperationException("USB NCM adapter did not become operational after the USB-only recovery pass.");
    }

    /// <summary>
    /// Legacy UI operation: USB transport first, network provisioning second.
    /// A network-sharing failure never tears down the USB transport.
    /// </summary>
    public async Task StartAsync()
    {
        var adapter = await EnsureUsbTransportAsync();
        WriteLog($"Network provisioning: USB transport ready on {adapter.Name}; starting ICS separately.");
        var recovered = await Task.Run(() => NetworkSharingRecovery.ApplySharingWithFallback(WriteLog));
        if (!recovered)
            throw new InvalidOperationException("USB NCM transport is up, but Windows Internet Connection Sharing could not be established. The USB transport itself was left untouched.");
        WriteLog("Network provisioning: ICS is active; USB transport remains independent.");
    }

    public Task StopAsync()
    {
        try
        {
            DisableAllIcs();
            var phone = FindAppleDevice();
            if (phone is not null) SetConfig(phone.Id, SafeIndexValue, "0");
            WriteLog("Transport stopped; ICS disabled and Apple USB configuration restored to safe mode.");
        }
        catch (Exception ex) { WriteLog($"Stop cleanup: {ex.GetType().Name}: {ex.Message}"); }
        return Task.CompletedTask;
    }

    public Task<Status> GetStatusAsync()
    {
        var p = FindAppleDevice();
        var a = FindPhoneAdapter();
        var lease = a is null ? null : FindIcsLease(a.Name);
        var sharing = a is not null && HasIcsLease(a.Name);
        var (rx, tx) = a is null ? (0d, 0d) : GetRates(a.Name);
        return Task.FromResult(new Status(p is not null, p?.Name ?? "Apple device", a?.Name, a?.OperationalStatus.ToString() ?? "—", sharing, lease, rx, tx));
    }

    public async Task<string> DiagnosticsAsync()
    {
        var sb = new StringBuilder();
        var p = FindAppleDevice();
        var a = FindPhoneAdapter();
        sb.AppendLine("Transport architecture: USB/NCM transport + separate network provisioning");
        sb.AppendLine($"Apple device: {(p is null ? "not connected" : p.Name)}");
        sb.AppendLine($"Apple PnP ID: {p?.Id ?? "—"}");
        sb.AppendLine($"USB identity: {UsbNative.GetDeviceId() ?? "unreachable"}");
        sb.AppendLine($"USB mode: {await UsbNative.GetModeAsync() ?? "unreachable"}");
        sb.AppendLine($"USB NCM functions: {string.Join(", ", await UsbNative.GetNcmControlInterfacesAsync())}");
        sb.AppendLine($"USB Ethernet: {a?.Name ?? "not present"} [{a?.OperationalStatus.ToString() ?? "—"}]");
        sb.AppendLine($"IPv4: {(a is null ? "—" : string.Join(", ", GetIpv4(a)))}");
        sb.AppendLine($"ICS lease: {(a is null ? "—" : FindIcsLease(a.Name) ?? "none")}");
        return sb.ToString();
    }

    private static async Task<string> WaitForModeAsync()
    {
        string? mode = null;
        for (var i = 0; i < 20; i++)
        {
            mode = await UsbNative.GetModeAsync();
            if (mode is not null) return mode;
            await Task.Delay(500);
        }
        throw new TimeoutException("Apple USB control interface did not return a mode.");
    }

    private async Task EnsureInboxNcmBindingAsync()
    {
        var infCandidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "usbncm.inf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "netncm.inf")
        }.Where(File.Exists).ToList();

        var inf = infCandidates.FirstOrDefault();
        if (inf is null)
        {
            try
            {
                inf = Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository"), "usbncm.inf", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch { }
        }

        if (inf is null)
        {
            WriteLog("USB transport: inbox UsbNcm INF not found; leaving existing NCM binding untouched.");
            return;
        }

        var add = Run("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"USB transport: UsbNcm package registration exit={add.ExitCode}");
        if (!string.IsNullOrWhiteSpace(add.Error)) WriteLog($"UsbNcm pnputil: {add.Error.Trim()}");

        var child = FindAppleInterfaceId(2);
        if (child is not null)
        {
            var restart = Run("pnputil.exe", $"/restart-device \"{child}\"");
            if (restart.ExitCode != 0 && restart.ExitCode != 3010)
                WriteLog($"USB transport: NCM child restart exit={restart.ExitCode}: {restart.Error.Trim()}");
        }
    }

    private static async Task<NetworkInterface?> WaitForPhoneAdapterAsync(int seconds)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            var adapter = FindPhoneAdapter();
            if (adapter?.OperationalStatus == OperationalStatus.Up) return adapter;
            await Task.Delay(500);
        }
        return null;
    }

    private static bool IsSafeMode(string mode) => mode is "3:3:3" or "3:3:3:0";
    private static bool IsNcmMode(string mode) => mode is "5:3:3" or "5:3:3:0";

    private static void ConfigureUsbCgpEnumerator(string pnpId)
    {
        using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnpId}", writable: true)
            ?? throw new InvalidOperationException("Cannot open Apple USB device registry key.");
        var drv = baseKey.GetValue("Driver") as string;
        if (string.IsNullOrWhiteSpace(drv)) throw new InvalidOperationException("Apple USB device has no usbccgp driver key.");
        using var sw = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{drv}", writable: true)
            ?? throw new InvalidOperationException($"Cannot open usbccgp software key {drv}.");
        sw.SetValue("EnumeratorClass", new byte[] { 0x02, 0x00, 0x00 }, Microsoft.Win32.RegistryValueKind.Binary);
        var lower = baseKey.GetValue("LowerFilters") as string[];
        if (lower is not null && lower.Contains("AppleLowerFilter", StringComparer.OrdinalIgnoreCase))
        {
            var remaining = lower.Where(x => !x.Equals("AppleLowerFilter", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == 0) baseKey.DeleteValue("LowerFilters", false);
            else baseKey.SetValue("LowerFilters", remaining, Microsoft.Win32.RegistryValueKind.MultiString);
        }
    }

    private static void SetConfig(string pnpId, string original, string alt)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnpId}\Device Parameters", writable: true)
            ?? throw new InvalidOperationException("Cannot open Apple USB device parameters.");
        key.SetValue("OriginalConfigurationValue", uint.Parse(original), Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("AltConfigurationValue", uint.Parse(alt), Microsoft.Win32.RegistryValueKind.DWord);
    }

    private static void DisablePhotoInterfaces()
    {
        foreach (var d in FindPnP("VID_05AC&PID_", "WPD"))
        {
            if (!d.Id.Contains("&MI_00\\", StringComparison.OrdinalIgnoreCase)) continue;
            var r = Run("pnputil.exe", $"/disable-device \"{d.Id}\"");
            if (r.ExitCode != 0 && r.ExitCode != 3010) throw new InvalidOperationException($"pnputil failed disabling photo interface ({r.ExitCode}): {r.Error}");
        }
    }

    private static void RestartDevice(string id)
    {
        var r = Run("pnputil.exe", $"/restart-device \"{id}\"");
        if (r.ExitCode != 0 && r.ExitCode != 3010) throw new InvalidOperationException($"pnputil failed restarting {id} ({r.ExitCode}): {r.Error}");
    }

    private static NetworkInterface? FindPhoneAdapter() => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
        n.OperationalStatus == OperationalStatus.Up &&
        n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
        (n.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Apple", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("NCM", StringComparison.OrdinalIgnoreCase)));

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

    private static PnpDevice? FindAppleDevice() => FindPnP("USB\\VID_05AC&PID_", null).FirstOrDefault(d => !d.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));

    private static string? FindAppleInterfaceId(int interfaceNumber)
    {
        var marker = $"&MI_{interfaceNumber:X2}\\";
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity");
            foreach (ManagementObject o in searcher.Get())
            {
                var id = o["PNPDeviceID"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id) && id.StartsWith("USB\\VID_05AC&PID_", StringComparison.OrdinalIgnoreCase) && id.Contains(marker, StringComparison.OrdinalIgnoreCase)) return id;
            }
        }
        catch { }
        return null;
    }

    private static string? FindIcsLease(string adapterName)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
        return nic?.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.Address.ToString()).FirstOrDefault(a => a.StartsWith(IcsSubnet, StringComparison.Ordinal));
    }

    private static bool HasIcsLease(string adapterName) => FindIcsLease(adapterName) is not null;

    private static string[] GetIpv4(NetworkInterface adapter) => adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToArray();

    private static (double Rx, double Tx) GetRates(string adapterName)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
            if (nic is null) return (0, 0);
            var stats = nic.GetIPv4Statistics();
            return (stats.BytesReceived / 1024d, stats.BytesSent / 1024d);
        }
        catch { return (0, 0); }
    }

    private static void DisableAllIcs()
    {
        try
        {
            var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (mgrType is null) return;
            dynamic mgr = Activator.CreateInstance(mgrType)!;
            foreach (var c in mgr.EnumEveryConnection())
            {
                try { dynamic cfg = mgr.INetSharingConfigurationForINetConnection(c); if ((bool)cfg.SharingEnabled) cfg.DisableSharing(); } catch { }
            }
        }
        catch { }
    }

    private static CommandResult Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return new CommandResult(-1, "", "process start failed");
            var output = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit();
            return new CommandResult(p.ExitCode, output, error);
        }
        catch (Exception ex) { return new CommandResult(-1, "", ex.Message); }
    }

    private static async Task WaitUntil(Func<bool> predicate, int seconds, string what)
    {
        for (var i = 0; i < seconds * 2; i++) { if (predicate()) return; await Task.Delay(500); }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public readonly record struct Status(bool AppleConnected, string AppleName, string? AdapterName, string AdapterStatus, bool Sharing, string? Lease, double Rx, double Tx);
    private readonly record struct CommandResult(int ExitCode, string Output, string Error);
    private sealed record PnpDevice(string Id, string Name);
}
