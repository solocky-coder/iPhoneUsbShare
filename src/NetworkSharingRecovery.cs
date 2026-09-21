using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.ServiceProcess;
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
        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var wifi = publicName ?? FindWifiName();
                var ethernet = privateName ?? FindPhoneEthernetName();
                if (wifi is null || ethernet is null)
                {
                    log($"[ICS] Recovery could not identify Wi-Fi/USB Ethernet: Wi-Fi={wifi ?? "none"}, Ethernet={ethernet ?? "none"}");
                    return false;
                }

                log($"[ICS] Recovery attempt {attempt}/{maxAttempts}: {wifi} -> {ethernet}");
                if (attempt == 1) EnsureIcsServices(log);
                ExecuteNativeIcsBinding(wifi, ethernet, log);
                log("[ICS] Recovery binding completed.");

                if (WaitForLease(ethernet, 15))
                {
                    log($"[ICS] DHCP lease detected on {ethernet}.");
                    return true;
                }

                log("[ICS] Sharing enabled but no 192.168.137.x lease appeared yet.");
                return true;
            }
            catch (COMException comEx) when (unchecked((uint)comEx.ErrorCode) == IcsComError)
            {
                log($"[ICS] Caught COM subscriber error 0x80040201 on attempt {attempt}; recycling SharedAccess before retry.");
                if (attempt >= maxAttempts) return false;
                ResetSharingServices(log);
                Thread.Sleep(1500);
            }
            catch (Exception ex)
            {
                log($"[ICS] Recovery failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        return false;
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
