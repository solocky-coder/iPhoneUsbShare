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
            // If the device's USB link dropped (the adapter is gone) no amount of retrying can work.
            if (!NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name.Equals(ethernet, StringComparison.OrdinalIgnoreCase)))
            {
                log($"[ICS] The USB Ethernet adapter \"{ethernet}\" no longer exists (the device's USB network link dropped); stopping.");
                return false;
            }

            var (viaPowerShell, strategy) = AttemptPlan[(attempt - 1) % AttemptPlan.Length];
            log($"[ICS] Enable attempt {attempt} ({(viaPowerShell ? "PowerShell" : "in-process")}, {strategy}): {wifi} -> {ethernet}");
            try
            {
                if (viaPowerShell) EnableViaPowerShell(wifi, ethernet, strategy, log);
                else ExecuteNativeIcsBinding(wifi, ethernet, log, strategy);
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

    // Order in which attempts are made (then repeats until the time budget is spent).
    private static readonly (bool PowerShell, IcsStrategy Strategy)[] AttemptPlan =
    {
        (false, IcsStrategy.PublicThenPrivate),
        (false, IcsStrategy.ResetAllThenPublicThenPrivate),
        (false, IcsStrategy.PrivateThenPublic),
        (true, IcsStrategy.PublicThenPrivate),
        (true, IcsStrategy.PrivateThenPublic),
    };

    // Same HNetCfg calls, made by powershell.exe (STA main thread, its own process). Connection names
    // travel in environment variables so no quoting is needed; the script is passed base64-encoded.
    private const string IcsPowerShellScript = @"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
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
  if ($env:ICS_ORDER -eq 'private-first') { $prv.EnableSharing(1); $pub.EnableSharing(0) }
  else { $pub.EnableSharing(0); $prv.EnableSharing(1) }
} catch {
  [Console]::Error.WriteLine(('HRESULT=0x{0:X8}: {1}' -f $_.Exception.HResult, $_.Exception.Message))
  exit 1
}
";

    private static void EnableViaPowerShell(string publicName, string privateName, IcsStrategy strategy, Action<string> log)
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
        psi.Environment["ICS_ORDER"] = strategy == IcsStrategy.PrivateThenPublic ? "private-first" : "public-first";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start powershell.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(true); } catch { }
            throw new System.TimeoutException("The PowerShell ICS helper did not finish within 30 seconds.");
        }
        var error = string.Join(" ", stderr.GetAwaiter().GetResult()
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#<", StringComparison.Ordinal) && !l.StartsWith("<Objs", StringComparison.Ordinal)));
        var output = stdout.GetAwaiter().GetResult().Trim();
        if (output.Length > 0) log($"[ICS] PowerShell output: {output}");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell ICS helper exited {process.ExitCode}: {(error.Length > 0 ? error : "no error text")}");
    }

    private static void ExecuteNativeIcsBinding(string publicConnectionName, string privateConnectionName, Action<string> log, IcsStrategy strategy) =>
        RunOnSta(() => ExecuteNativeIcsBindingCore(publicConnectionName, privateConnectionName, log, strategy));

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

    // How one enable attempt is made. Each is a different way of asking HNetCfg for the same result,
    // because the GUI's "Allow other network users to connect..." works on machines where the plain
    // public-then-private API sequence fails with 0x80040201.
    private enum IcsStrategy
    {
        /// <summary>Clear our two connections, enable public, then private.</summary>
        PublicThenPrivate,
        /// <summary>Clear our two connections, enable private, then public.</summary>
        PrivateThenPublic,
        /// <summary>Clear sharing on EVERY connection (stale/other ICS such as a hotspot), wait, re-open the
        /// ICS manager, then public then private.</summary>
        ResetAllThenPublicThenPrivate,
    }

    private sealed class IcsConnections
    {
        public object? Public;
        public object? Private;
        public readonly List<(string Name, object Cfg, bool Sharing)> All = new();
    }

    private static IcsConnections ResolveConnections(string publicName, string privateName, Action<string>? verboseLog)
    {
        var mgrType = Type.GetTypeFromProgID("HNetCfg.HNetShare")
            ?? throw new InvalidOperationException("Windows Internet Connection Sharing is unavailable.");
        dynamic mgr = Activator.CreateInstance(mgrType)!;
        var result = new IcsConnections();
        foreach (var connection in mgr.EnumEveryConnection())
        {
            dynamic props = mgr.NetConnectionProps(connection);
            var name = (string)props.Name;
            object? cfgObject = null;
            var sharing = false;
            var sharingType = -1;
            try
            {
                dynamic cfg = mgr.INetSharingConfigurationForINetConnection(connection);
                cfgObject = cfg;
                sharing = (bool)cfg.SharingEnabled;
                if (sharing) sharingType = (int)cfg.SharingConnectionType;
            }
            catch { }
            if (verboseLog is not null)
            {
                string device = "?", status = "?";
                try { device = (string)props.DeviceName; status = ((int)props.Status).ToString(); } catch { }
                verboseLog($"[ICS] Connection: {name} | device={device} | status={status} | sharing={(sharing ? (sharingType == 0 ? "PUBLIC" : sharingType == 1 ? "PRIVATE" : "on") : "off")}");
            }
            if (cfgObject is null) continue;
            result.All.Add((name, cfgObject, sharing));
            if (name.Equals(publicName, StringComparison.OrdinalIgnoreCase)) result.Public = cfgObject;
            else if (name.Equals(privateName, StringComparison.OrdinalIgnoreCase)) result.Private = cfgObject;
        }
        return result;
    }

    private static void ExecuteNativeIcsBindingCore(string publicName, string privateName, Action<string> log, IcsStrategy strategy)
    {
        log($"[ICS] Strategy: {strategy}");
        var state = ResolveConnections(publicName, privateName, log);
        if (state.Public is null || state.Private is null)
            throw new InvalidOperationException($"Windows ICS did not expose {(state.Public is null ? publicName : "")}{(state.Public is null && state.Private is null ? " and " : "")}{(state.Private is null ? privateName : "")}.");

        if (strategy == IcsStrategy.ResetAllThenPublicThenPrivate)
        {
            foreach (var (name, cfg, sharing) in state.All.Where(c => c.Sharing))
            {
                try { ((dynamic)cfg).DisableSharing(); log($"[ICS] Cleared sharing on {name}."); }
                catch (Exception ex) { log($"[ICS] Could not clear sharing on {name}: {ex.GetType().Name}: {ex.Message}"); }
            }
            Thread.Sleep(2500);
            state = ResolveConnections(publicName, privateName, null);
            if (state.Public is null || state.Private is null)
                throw new InvalidOperationException("Windows ICS lost the Wi-Fi/USB Ethernet connection after clearing sharing.");
        }
        else
        {
            foreach (var (name, cfg, sharing) in state.All.Where(c => c.Sharing &&
                         (c.Name.Equals(publicName, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(privateName, StringComparison.OrdinalIgnoreCase))))
            {
                try { ((dynamic)cfg).DisableSharing(); log($"[ICS] Cleared existing sharing on {name}."); }
                catch (Exception ex) { log($"[ICS] DisableSharing({name}) failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            Thread.Sleep(1000);
            state = ResolveConnections(publicName, privateName, null);
            if (state.Public is null || state.Private is null)
                throw new InvalidOperationException("Windows ICS lost the Wi-Fi/USB Ethernet connection after clearing sharing.");
        }

        void EnablePublic()
        {
            try { ((dynamic)state.Public!).EnableSharing(0); }
            catch (Exception ex) { log($"[ICS] EnableSharing(public) on {publicName} failed: {ex.GetType().Name} HRESULT=0x{ex.HResult:X8}: {ex.Message}"); throw; }
        }
        void EnablePrivate()
        {
            try { ((dynamic)state.Private!).EnableSharing(1); }
            catch (Exception ex) { log($"[ICS] EnableSharing(private) on {privateName} failed: {ex.GetType().Name} HRESULT=0x{ex.HResult:X8}: {ex.Message}"); throw; }
        }

        if (strategy == IcsStrategy.PrivateThenPublic) { EnablePrivate(); EnablePublic(); }
        else { EnablePublic(); EnablePrivate(); }
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

                // Phase 1: read the state of our connections without changing anything.
                var targets = new List<(string Name, object Cfg, bool Sharing, int Type)>();
                foreach (var connection in mgr.EnumEveryConnection())
                {
                    dynamic props = mgr.NetConnectionProps(connection);
                    var name = (string)props.Name;
                    if (!names.Contains(name)) continue;
                    try
                    {
                        dynamic cfg = mgr.INetSharingConfigurationForINetConnection(connection);
                        var sharing = (bool)cfg.SharingEnabled;
                        targets.Add((name, (object)cfg, sharing, sharing ? (int)cfg.SharingConnectionType : -1));
                    }
                    catch (Exception ex) { log($"[ICS] Could not read sharing state of {name}: {ex.GetType().Name}: {ex.Message}"); }
                }

                // Phase 2: switching the public side off tears the whole ICS pairing down, and Windows then
                // reconfigures the private adapter. Do that first and stop touching ICS afterwards (querying
                // the private connection at that moment is what used to hang). Only private-only leftovers
                // are cleared individually.
                foreach (var target in targets.Where(t => t.Sharing).OrderBy(t => t.Type == 0 ? 0 : 1))
                {
                    try
                    {
                        ((dynamic)target.Cfg).DisableSharing();
                        log($"[ICS] Disabled sharing on {target.Name}.");
                    }
                    catch (Exception ex) { log($"[ICS] Could not disable sharing on {target.Name}: {ex.GetType().Name}: {ex.Message}"); }
                    if (target.Type == 0) break;
                }
            }, timeoutMs: 10000);
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
    private static void RunOnSta(Action work, int timeoutMs = 30000)
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
        // A hung HNetCfg call must never freeze the app (it did: Stop blocked the UI thread for good
        // right after ICS was switched off). The thread is a background thread, so abandoning it on
        // timeout cannot keep the process alive.
        if (!thread.Join(timeoutMs))
            throw new System.TimeoutException($"Windows ICS did not respond within {timeoutMs / 1000} s.");
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
