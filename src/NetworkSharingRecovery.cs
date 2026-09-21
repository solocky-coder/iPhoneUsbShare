using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace iPhoneUsbShare;

internal static class NetworkSharingRecovery
{
    private const uint IcsComError = 0x80040201;
    private const string Subnet = "192.168.137.";

    public static bool IsSubscriberError(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is COMException com && unchecked((uint)com.ErrorCode) == IcsComError)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Enables Windows ICS from <paramref name="publicName"/> (the uplink) to
    /// <paramref name="privateName"/> (the device's USB Ethernet adapter).
    /// When a name is omitted it is auto-detected as before (first Wi-Fi /
    /// first Apple-or-NCM Ethernet adapter); the engine always passes both so
    /// an unrelated wired adapter can never become the ICS private side.
    /// </summary>
    public static bool ApplySharingWithFallback(Action<string> log, string? publicName = null, string? privateName = null)
    {
        var wifi = publicName ?? FindWifiName();
        var ethernet = privateName ?? FindPhoneEthernetName();
        if (wifi is null || ethernet is null)
        {
            log($"[ICS] Could not identify Wi-Fi/USB Ethernet: Wi-Fi={wifi ?? "none"}, Ethernet={ethernet ?? "none"}");
            return false;
        }

        EnsureIcsServices(log);

        // A USB adapter that was created a moment ago is often not ready for ICS yet (network
        // identification, address configuration), and HNetCfg then fails with 0x80040201 even though
        // sharing it by hand a little later works. So keep retrying for up to a minute, alternating the
        // in-process COM call with an out-of-process PowerShell call (a different apartment/process
        // environment), and recycle SharedAccess once if it keeps failing.
        var deadline = DateTime.UtcNow.AddSeconds(IcsRetryBudgetSeconds);
        var recycled = false;
        for (var attempt = 1; ; attempt++)
        {
            var viaPowerShell = attempt % 2 == 0;
            log($"[ICS] Enable attempt {attempt} ({(viaPowerShell ? "PowerShell" : "in-process")}): {wifi} -> {ethernet}");
            try
            {
                if (viaPowerShell) EnableViaPowerShell(wifi, ethernet, log);
                else ExecuteNativeIcsBinding(wifi, ethernet, log);
                log("[ICS] Binding completed.");

                if (WaitForLease(ethernet, 15))
                    log($"[ICS] DHCP lease detected on {ethernet}.");
                else
                    log("[ICS] Sharing enabled but no 192.168.137.x lease appeared yet.");
                return true;
            }
            catch (Exception ex)
            {
                var hint = IsSubscriberError(ex) || ex.Message.Contains("80040201", StringComparison.OrdinalIgnoreCase)
                    ? " (0x80040201: ICS event subscribers failed; the adapter or a required service may not be ready yet)"
                    : "";
                log($"[ICS] Attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}{hint}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                log($"[ICS] Giving up after {IcsRetryBudgetSeconds}s of retries.");
                return false;
            }
            if (!recycled && attempt >= 3)
            {
                ResetSharingServices(log);
                recycled = true;
            }
            Thread.Sleep(4000);
        }
    }

    private const int IcsRetryBudgetSeconds = 60;

    // Same HNetCfg calls, made by powershell.exe (STA main thread, its own process). Connection names
    // travel in environment variables so no quoting is needed; the script is passed base64-encoded.
    private const string IcsPowerShellScript = @"
$ErrorActionPreference = 'Stop'
try {
  $m = New-Object -ComObject HNetCfg.HNetShare
  $pub = $null; $prv = $null
  foreach ($c in $m.EnumEveryConnection) {
    $p = $m.NetConnectionProps.Invoke($c)
    if ($p.Name -eq $env:ICS_PUBLIC) { $pub = $m.INetSharingConfigurationForINetConnection.Invoke($c) }
    elseif ($p.Name -eq $env:ICS_PRIVATE) { $prv = $m.INetSharingConfigurationForINetConnection.Invoke($c) }
  }
  if ($null -eq $pub -or $null -eq $prv) { throw 'ICS did not expose the public and private connections.' }
  if ($pub.SharingEnabled) { $pub.DisableSharing() }
  if ($prv.SharingEnabled) { $prv.DisableSharing() }
  $pub.EnableSharing(0)
  $prv.EnableSharing(1)
} catch {
  [Console]::Error.WriteLine(('HRESULT=0x{0:X8}: {1}' -f $_.Exception.HResult, $_.Exception.Message))
  exit 1
}
";

    private static void EnableViaPowerShell(string publicName, string privateName, Action<string> log)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(IcsPowerShellScript));
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -STA -OutputFormat Text -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ICS_PUBLIC"] = publicName;
        psi.Environment["ICS_PRIVATE"] = privateName;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start powershell.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("The PowerShell ICS helper did not finish within 30 seconds.");
        }
        var error = stderr.GetAwaiter().GetResult().Trim();
        var output = stdout.GetAwaiter().GetResult().Trim();
        if (output.Length > 0) log($"[ICS] PowerShell output: {output}");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell ICS helper exited {process.ExitCode}: {(error.Length > 0 ? error : "no error text")}");
    }

    private static void ExecuteNativeIcsBinding(string publicConnectionName, string privateConnectionName, Action<string> log) =>
        RunOnSta(() => ExecuteNativeIcsBindingCore(publicConnectionName, privateConnectionName, log));

    // 0x80040201 (EVENT_E_ALL_SUBSCRIBERS_FAILED) from HNetCfg usually means one of the services ICS
    // relies on (Windows Firewall / Base Filtering Engine, Network Connections, network list) is not
    // running. Log their state and start the firewall pair if they are merely stopped.
    private static readonly (string Name, string Label, bool TryStart)[] IcsServices =
    {
        ("BFE", "Base Filtering Engine", true),
        ("MpsSvc", "Windows Defender Firewall", true),
        ("SharedAccess", "Internet Connection Sharing", false),
        ("Netman", "Network Connections", false),
        ("NlaSvc", "Network Location Awareness", false),
        ("netprofm", "Network List Service", false),
    };

    private static void EnsureIcsServices(Action<string> log)
    {
        foreach (var (name, label, tryStart) in IcsServices)
        {
            try
            {
                using var sc = new ServiceController(name);
                var status = sc.Status;
                var startType = sc.StartType;
                log($"[ICS] Service {name} ({label}): {status}, start type {startType}");
                if (tryStart && status == ServiceControllerStatus.Stopped && startType != ServiceStartMode.Disabled)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    log($"[ICS] Started {name}.");
                }
            }
            catch (Exception ex) { log($"[ICS] Service {name} check failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private static void ExecuteNativeIcsBindingCore(string publicConnectionName, string privateConnectionName, Action<string> log)
    {
        var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare")
            ?? throw new InvalidOperationException("Windows Internet Connection Sharing is unavailable.");
        dynamic mgr = Activator.CreateInstance(mgrType)!;
        dynamic? publicCfg = null;
        dynamic? privateCfg = null;

        foreach (var connection in mgr.EnumEveryConnection())
        {
            dynamic props = mgr.NetConnectionProps(connection);
            var name = (string)props.Name;
            try { log($"[ICS] Connection: {name} | device={(string)props.DeviceName} | status={(int)props.Status}"); } catch { }
            if (name.Equals(publicConnectionName, StringComparison.OrdinalIgnoreCase))
                publicCfg = mgr.INetSharingConfigurationForINetConnection(connection);
            else if (name.Equals(privateConnectionName, StringComparison.OrdinalIgnoreCase))
                privateCfg = mgr.INetSharingConfigurationForINetConnection(connection);
        }

        if (publicCfg is null || privateCfg is null)
            throw new InvalidOperationException("Windows ICS did not expose the Wi-Fi and USB Ethernet adapters.");

        try { if ((bool)publicCfg.SharingEnabled) { log($"[ICS] Clearing existing sharing on {publicConnectionName}."); publicCfg.DisableSharing(); } }
        catch (Exception ex) { log($"[ICS] DisableSharing({publicConnectionName}) failed: {ex.GetType().Name}: {ex.Message}"); }
        try { if ((bool)privateCfg.SharingEnabled) { log($"[ICS] Clearing existing sharing on {privateConnectionName}."); privateCfg.DisableSharing(); } }
        catch (Exception ex) { log($"[ICS] DisableSharing({privateConnectionName}) failed: {ex.GetType().Name}: {ex.Message}"); }

        try { publicCfg.EnableSharing(0); }
        catch (Exception ex) { log($"[ICS] EnableSharing(public) on {publicConnectionName} failed: {ex.GetType().Name} HRESULT=0x{ex.HResult:X8}: {ex.Message}"); throw; }
        try { privateCfg.EnableSharing(1); }
        catch (Exception ex) { log($"[ICS] EnableSharing(private) on {privateConnectionName} failed: {ex.GetType().Name} HRESULT=0x{ex.HResult:X8}: {ex.Message}"); throw; }
    }

    /// <summary>
    /// Turns ICS off on the named connections (the uplink and the device's USB
    /// Ethernet adapter). Connections that no longer exist (device unplugged)
    /// are simply not found and skipped.
    /// </summary>
    public static void DisableSharing(Action<string> log, params string?[] connectionNames)
    {
        var names = connectionNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return;

        try
        {
            RunOnSta(() =>
            {
                var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare")
                    ?? throw new InvalidOperationException("Windows Internet Connection Sharing is unavailable.");
                dynamic mgr = Activator.CreateInstance(mgrType)!;
                foreach (var connection in mgr.EnumEveryConnection())
                {
                    dynamic props = mgr.NetConnectionProps(connection);
                    var name = (string)props.Name;
                    if (!names.Contains(name)) continue;
                    dynamic cfg = mgr.INetSharingConfigurationForINetConnection(connection);
                    try
                    {
                        if ((bool)cfg.SharingEnabled)
                        {
                            cfg.DisableSharing();
                            log($"[ICS] Disabled sharing on {name}.");
                        }
                    }
                    catch (Exception ex) { log($"[ICS] Could not disable sharing on {name}: {ex.GetType().Name}: {ex.Message}"); }
                }
            });
        }
        catch (Exception ex) { log($"[ICS] Disable failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Picks the connection to share: an up, non-loopback/tunnel adapter that has
    /// an IPv4 default gateway (i.e. actually reaches the internet), other than the
    /// device's own adapter. Wi-Fi is preferred, matching the documented behaviour.
    /// </summary>
    internal static string? FindPublicConnectionName(string phoneAdapterName)
    {
        var uplinks = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        !n.Name.Equals(phoneAdapterName, StringComparison.OrdinalIgnoreCase) &&
                        HasIpv4Gateway(n))
            .ToList();
        return (uplinks.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                ?? uplinks.FirstOrDefault())?.Name;
    }

    private static bool HasIpv4Gateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !g.Address.Equals(System.Net.IPAddress.Any));
        }
        catch { return false; }
    }

    // HNetCfg.HNetShare is an apartment-threaded COM object. Callers of the engine
    // may be on the WPF UI thread or a thread-pool (MTA) thread, so all ICS COM work
    // runs on its own short-lived STA thread; exceptions (including COMException,
    // which ApplySharingWithFallback matches on) are rethrown with their type intact.
    private static void RunOnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        })
        { IsBackground = true, Name = "ICS-STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void ResetSharingServices(Action<string> log)
    {
        try
        {
            using var sc = new ServiceController("SharedAccess");
            log($"[ICS] SharedAccess status before reset: {sc.Status}");
            if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
            log("[ICS] SharedAccess service successfully recycled.");
        }
        catch (Exception ex)
        {
            log($"[ICS] SharedAccess recycle failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? FindWifiName()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up)
            ?.Name;
    }

    private static string? FindPhoneEthernetName()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                                 n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                                 (n.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                                  n.Description.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
                                  n.Description.Contains("NCM", StringComparison.OrdinalIgnoreCase)))
            ?.Name;
    }

    private static bool WaitForLease(string adapterName, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            if (HasLease(adapterName)) return true;
            Thread.Sleep(1000);
        }
        return HasLease(adapterName);
    }

    private static bool HasLease(string adapterName)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
            return nic?.GetIPProperties().UnicastAddresses.Any(a =>
                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                a.Address.ToString().StartsWith(Subnet, StringComparison.Ordinal)) == true;
        }
        catch { return false; }
    }
}
