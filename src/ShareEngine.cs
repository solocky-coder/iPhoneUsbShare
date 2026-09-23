using Microsoft.Win32;
using System.Collections.Concurrent;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

public sealed class ShareEngine
{
    public event EventHandler<string>? Log;
    private const string Vendor = "05AC";
    private const string SafeIndexValue = "2";
    private const string NcmIndexValue = "4";
    // Usbccgp configuration selection for the NCM function. NcmIndexValue ("4") is the registry
    // *index* of the mode-5 configuration that carries CDC-NCM (the fifth descriptor: 6 interfaces,
    // NCM control = interface 2); NcmAltValue is the AltConfigurationValue paired with it, and
    // SafeIndexValue/"0" is the pair used while talking to MI_00. Evidence that this value is an
    // index, not a bConfigurationValue: with OriginalConfigurationValue=2 usbccgp exposes the
    // PTP + usbmux children (the third descriptor), and 5 is out of range (only indices 0-4 exist),
    // which leaves the composite with no children at all.
    private const string NcmAltValue = "2";
    private const int MaxDirectUsbDevices = 4;
    private static readonly string[] HostAddresses = { "192.168.99.1", "192.168.100.1", "192.168.101.1", "192.168.102.1" };
    private static readonly string[] PeerAddresses = { "192.168.99.2", "192.168.100.2", "192.168.101.2", "192.168.102.2" };
    private static readonly object LogFileLock = new();
    private readonly SemaphoreSlim _startStopLock = new(1, 1);
    // Concurrent: teardown runs on a worker thread (ICS/registry calls can block) while the UI timer reads it.
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private sealed record ActiveSession(UsbNative.AppleUsbTarget Target, NetworkInterface Adapter, ShareMode Mode, string HostAddress, string PeerAddress, IsolatedDhcpServer? Dhcp, string? IcsPublicName);
    private ShareMode _mode;
    private string AppDir => AppContext.BaseDirectory;
    private string ActivityLogPath => Path.Combine(AppDir, "ActivityLog.txt");
    private string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iPhoneUsbShare");

    /// <summary>
    /// How the USB Ethernet link is used. Both modes run the same USB bring-up; see <see cref="ShareMode"/>.
    /// Can only be changed while no session is active.
    /// </summary>
    public ShareMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            if (_sessions.Count > 0) throw new InvalidOperationException("Stop sharing before changing the mode.");
            _mode = value;
            WriteLog($"Share mode: {value.DisplayName()}");
        }
    }

    public ShareEngine(ShareMode mode = ShareMode.DirectUsb)
    {
        _mode = mode;
        WriteLog("============================================================");
        WriteLog("iPhoneUsbShare session started");
        WriteLog($"Share mode: {mode.DisplayName()}");
        WriteLog($"Application directory: {AppDir}");
        WriteLog($"Activity log: {ActivityLogPath}");
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

        // Do not gate startup on the first Apple device returned by WMI.
        // Multiple iPhones/iPads can be attached at once, and one device may
        // temporarily be between USB configurations while another is already
        // reachable through WinUSB. EnumerateTargets() is target-aware and also
        // performs the per-device WinUSB migration when an MI_00 path is missing.
        var targets = UsbNative.EnumerateTargets();
        var reachable = targets.Count(UsbNative.IsReachable);
        if (reachable == 0)
        {
            throw new InvalidOperationException(
                "No Apple USB control interface is reachable through WinUSB. " +
                "The application will not install or load the legacy libusb-win32 filter. " +
                "Verify that at least one connected Apple control interface has the iPhoneUsbShare WinUSB driver bound, then reconnect the device.");
        }

        WriteLog($"WinUSB control path is available for {reachable}/{targets.Length} Apple USB device(s); no legacy libusb-win32 driver will be installed.");
        return Task.CompletedTask;
    }

    public async Task StartAsync()
    {
        await _startStopLock.WaitAsync();
        try { await StartAllAsync(); }
        finally { _startStopLock.Release(); }
    }

    private async Task StartAllAsync()
    {
        await WaitUntil(() => UsbNative.EnumerateTargets().Length > 0, 15, "Apple USB devices");
        var targets = UsbNative.EnumerateTargets();
        var reverse = _mode == ShareMode.ReverseTethering;
        // Windows ICS has exactly one private connection, so reverse tethering serves one device at a time.
        var maxSessions = reverse ? 1 : MaxDirectUsbDevices;
        for (var index = 0; index < targets.Length && _sessions.Count < maxSessions; index++)
        {
            var target = NormalizeTargetParent(targets[index]);
            if (_sessions.ContainsKey(target.DeviceKey)) continue;
            var slot = reverse ? 0 : FindFreeSlot();
            if (slot < 0) break;
            try { await StartCoreAsync(target, slot); }
            catch (Exception ex) { WriteLog($"Apple USB session {target.ParentId} failed: {ex.Message}"); }
        }
        WriteLog($"Direct USB device limit: {MaxDirectUsbDevices}; discovered={targets.Length}, active={_sessions.Count}.");
        if (_sessions.Count == 0) throw new InvalidOperationException("No Apple USB networking session could be started.");
    }

    private static UsbNative.AppleUsbTarget NormalizeTargetParent(UsbNative.AppleUsbTarget target)
    {
        if (!target.ParentId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            return target;

        var actualParent = FindDeviceParentId(target.ParentId);
        if (string.IsNullOrWhiteSpace(actualParent))
        {
            WriteStaticLog($"Apple USB target normalization: could not resolve composite parent for {target.ParentId}");
            return target;
        }

        WriteStaticLog($"Apple USB target normalization: parent={target.ParentId} -> compositeParent={actualParent}");
        return target with { ParentId = actualParent };
    }

    private int FindFreeSlot()
    {
        for (var i = 0; i < HostAddresses.Length; i++)
            if (!_sessions.Values.Any(session => session.HostAddress.Equals(HostAddresses[i], StringComparison.OrdinalIgnoreCase)))
                return i;
        return -1;
    }

    private async Task StartCoreAsync(UsbNative.AppleUsbTarget target, int slot)
    {
        var shareMode = _mode;
        var phone = FindAppleDevice(target.ParentId) ?? throw new InvalidOperationException($"Apple device not found: {target.ParentId}");
        if (shareMode == ShareMode.ReverseTethering)
            WriteLog($"Apple device found: {phone.Id} ({phone.Name}); reverse tethering (Windows ICS)");
        else
            WriteLog($"Apple device found: {phone.Id} ({phone.Name}); USB slot {slot + 1}, host={HostAddresses[slot]}, peer={PeerAddresses[slot]}");
        DisablePhotoInterfaces();
        var adapter = FindPhoneAdapter(target.ParentId);
        if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
        {
            ConfigureUsbCgpEnumerator(phone.Id);
            SetConfig(phone.Id, SafeIndexValue, "0");
            RestartDevice(phone.Id);
            await WaitUntil(() => FindAppleDevice(target.ParentId) is not null, 25, "Apple USB device to re-enumerate");
            DisablePhotoInterfaces();
            string? mode = null;
            for (var i = 0; i < 20; i++) { mode = await UsbNative.GetModeAsync(target); if (mode is not null) break; await Task.Delay(1000); }
            if (mode is null) throw new InvalidOperationException("Apple USB control interface did not reappear.");
            var ncmSelected = false;
            try
            {
                if (mode != "5:3:3:0" && mode != "5:3:3")
                {
                    if (mode != "3:3:3:0" && mode != "3:3:3") throw new InvalidOperationException($"Unexpected Apple USB mode: {mode}.");
                    // Point usbccgp at the NCM configuration *before* the mode switch, so the device
                    // re-enumerates straight into it. Left at the safe index 2, usbccgp comes back
                    // with only MI_00/MI_01 and the NCM child (MI_02) is never created.
                    WriteLog($"Selecting NCM configuration (OriginalConfigurationValue={NcmIndexValue}, AltConfigurationValue={NcmAltValue}) before mode switch.");
                    SetConfig(phone.Id, NcmIndexValue, NcmAltValue);
                    ncmSelected = true;
                    if (!await UsbNative.SetConfigurationAsync(target, int.Parse(NcmIndexValue))) throw new InvalidOperationException("Apple CDC-NCM configuration switch failed.");
                    if (!await UsbNative.SetModeAsync(target, 3)) throw new InvalidOperationException("Apple device rejected CDC-NCM mode.");
                }
                else
                {
                    // The device is already in mode 5 (left over from an earlier run) but usbccgp was
                    // just restarted on the safe configuration above; move it to the NCM configuration.
                    WriteLog($"Apple device already in mode {mode}; selecting NCM configuration (OriginalConfigurationValue={NcmIndexValue}, AltConfigurationValue={NcmAltValue}) and restarting the composite device.");
                    SetConfig(phone.Id, NcmIndexValue, NcmAltValue);
                    ncmSelected = true;
                    RestartDevice(phone.Id);
                    await WaitUntil(() => FindAppleDevice(target.ParentId) is not null, 25, "Apple USB device to re-enumerate on the NCM configuration");
                    await Task.Delay(1500);
                }
                await BindAppleOrInboxNcmDriverAsync(target);
                await WaitUntil(() => FindPhoneAdapter(target.ParentId)?.OperationalStatus == OperationalStatus.Up, 30, "USB Ethernet adapter");
                adapter = FindPhoneAdapter(target.ParentId) ?? throw new InvalidOperationException("USB Ethernet adapter did not start.");
            }
            catch
            {
                // A failed NCM attempt must not leave usbccgp pointed at the NCM configuration:
                // if that configuration does not enumerate, the composite comes up with no
                // children (no MI_00, no WinUSB) and every later start times out.
                if (ncmSelected)
                {
                    try
                    {
                        SetConfig(phone.Id, SafeIndexValue, "0");
                        RestartDevice(phone.Id);
                        WriteLog("NCM bring-up failed; restored the safe usbccgp configuration and restarted the composite device.");
                    }
                    catch (Exception restoreEx) { WriteLog($"Could not restore the safe usbccgp configuration: {restoreEx.Message}"); }
                }
                throw;
            }
        }
        if (shareMode == ShareMode.ReverseTethering) await StartReverseTetheringAsync(target, adapter);
        else await StartDirectUsbAsync(target, adapter, slot);
    }

    // Direct USB: isolated static IPv4 + built-in DHCP lease, no gateway/DNS/ICS.
    private async Task StartDirectUsbAsync(UsbNative.AppleUsbTarget target, NetworkInterface adapter, int slot)
    {
        var hostAddress = HostAddresses[slot];
        var peerAddress = PeerAddresses[slot];
        await ConfigureStaticNetworkAsync(adapter.Name, hostAddress);
        await WaitUntil(() => HasAddress(adapter.Name, hostAddress), 15, "static USB IPv4 address");
        // Until duplicate-address detection finishes the address cannot be bound; wait for "preferred".
        try { await WaitUntil(() => IsAddressPreferred(adapter.Name, hostAddress), 8, "static USB IPv4 address to become preferred"); }
        catch (TimeoutException ex) { WriteLog($"{ex.Message} Continuing; the DHCP server retries its bind."); }
        var dhcp = new IsolatedDhcpServer(WriteLog, hostAddress, peerAddress);
        dhcp.Start();
        _sessions[target.DeviceKey] = new ActiveSession(target, adapter, ShareMode.DirectUsb, hostAddress, peerAddress, dhcp, null);
        WriteLog($"Isolated USB network ready: Windows={hostAddress}, iPhone={peerAddress}, mask=255.255.255.0, no gateway, no DNS, no ICS.");
    }

    // Reverse tethering: Windows ICS shares the PC's uplink with the device's USB Ethernet adapter.
    // ICS assigns the private side 192.168.137.1 and runs its own DHCP/NAT for the device.
    private async Task StartReverseTetheringAsync(UsbNative.AppleUsbTarget target, NetworkInterface adapter)
    {
        // A previous Direct USB run leaves a static 192.168.99-102.1 address on the adapter; return the
        // adapter to automatic addressing so ICS can own its configuration.
        if (HostAddresses.Any(host => HasAddress(adapter.Name, host)))
        {
            WriteLog($"Reverse tethering: clearing the Direct USB static address on {adapter.Name} before enabling ICS.");
            await ResetAdapterToAutomaticAsync(adapter.Name);
        }

        var publicName = NetworkSharingRecovery.FindPublicConnectionName(adapter.Name)
            ?? throw new InvalidOperationException("Reverse tethering needs an internet-connected adapter (Wi-Fi preferred) to share, but none with a default gateway is up. Connect this PC to the internet or switch to Direct USB mode.");
        WriteLog($"Reverse tethering: enabling Windows ICS {publicName} -> {adapter.Name}");
        var ok = await Task.Run(() => NetworkSharingRecovery.ApplySharingWithFallback(WriteLog, publicName, adapter.Name));
        if (!ok)
        {
            // A half-applied ICS binding (public side enabled, private side not) must not be left behind.
            await Task.Run(() => NetworkSharingRecovery.DisableSharing(WriteLog, publicName, adapter.Name));
            throw new InvalidOperationException("Windows Internet Connection Sharing could not be enabled. See ActivityLog.txt ([ICS] lines).");
        }

        _sessions[target.DeviceKey] = new ActiveSession(target, adapter, ShareMode.ReverseTethering, "", "", null, publicName);
        WriteLog($"Reverse tethering ready: {publicName} shared to {adapter.Name} via Windows ICS (192.168.137.x).");
    }

    private void ReleaseSession(ActiveSession session)
    {
        try { session.Dhcp?.Dispose(); } catch { }
        if (session.Mode == ShareMode.ReverseTethering)
        {
            try { NetworkSharingRecovery.DisableSharing(WriteLog, session.IcsPublicName, session.Adapter.Name); }
            catch (Exception ex) { WriteLog($"ICS cleanup {session.Adapter.Name}: {ex.Message}"); }
        }
    }

    public async Task StopAsync()
    {
        await _startStopLock.WaitAsync();
        // Teardown makes blocking ICS/registry calls; keep them off the UI thread so the window never freezes.
        try { await Task.Run(() => StopAllCore(restoreUsbConfiguration: true)); }
        finally { _startStopLock.Release(); }
    }

    /// <summary>
    /// Changes the mode at any time. When sessions are running they are torn down
    /// (DHCP server stopped / ICS disabled) and started again in the new mode under
    /// one lock. The USB Ethernet adapter stays up, so the NCM bring-up is skipped and
    /// the switch only reconfigures the network side. If the restart fails the new mode
    /// is still selected and the exception is thrown to the caller.
    /// </summary>
    public async Task SwitchModeAsync(ShareMode newMode)
    {
        await _startStopLock.WaitAsync();
        try
        {
            if (newMode == _mode) return;
            var wasSharing = _sessions.Count > 0;
            if (wasSharing) await Task.Run(() => StopAllCore(restoreUsbConfiguration: false));
            _mode = newMode;
            WriteLog($"Share mode: {newMode.DisplayName()}");
            if (wasSharing) await StartAllAsync();
        }
        finally { _startStopLock.Release(); }
    }

    private void StopAllCore(bool restoreUsbConfiguration)
    {
        var hadReverse = _sessions.Values.Any(session => session.Mode == ShareMode.ReverseTethering);
        foreach (var session in _sessions.Values.ToArray())
        {
            ReleaseSession(session);
            if (!restoreUsbConfiguration) continue;
            try { var phone = FindAppleDevice(session.Target.ParentId); if (phone is not null) SetConfig(phone.Id, SafeIndexValue, "0"); } catch (Exception ex) { WriteLog($"Stop cleanup {session.Target.ParentId}: {ex.Message}"); }
        }
        _sessions.Clear();
        if (!restoreUsbConfiguration)
            WriteLog(hadReverse ? "Network side stopped; Windows ICS disabled (mode switch)." : "Network side stopped (mode switch).");
        else
            WriteLog(hadReverse
                ? "Sharing stopped; Windows ICS disabled and Apple USB configurations restored."
                : "Sharing stopped; Apple USB configurations restored. No ICS/NAT/gateway/DNS state is modified.");
    }

    private static async Task WaitForAppleUsbReadyAsync()
    {
        await WaitUntil(() => UsbNative.EnumerateTargets().Length > 0, 15, "Apple USB devices");
    }

    public async Task<Status> GetStatusAsync()
    {
        ReconcileRemovedSessions();

        var sessions = _sessions.Values
            .OrderBy(s => s.HostAddress, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var (rx, tx) = GetRates(s.Adapter.Name);
                return new SessionStatus(
                    s.HostAddress,
                    s.PeerAddress,
                    s.Adapter.Name,
                    s.Adapter.OperationalStatus.ToString(),
                    FindAddress(s.Adapter.Name),
                    rx,
                    tx);
            })
            .ToArray();

        var p = FindAppleDevice();
        var a = _sessions.Values.FirstOrDefault()?.Adapter ?? FindPhoneAdapter();
        var sharing = _sessions.Count > 0;
        var (rx, tx) = a is null ? (0d, 0d) : GetRates(a.Name);

        return await Task.FromResult(new Status(
            p is not null,
            p?.Name ?? "Apple device",
            a?.Name,
            a?.OperationalStatus.ToString() ?? "—",
            sharing,
            a is null ? null : FindAddress(a.Name),
            rx,
            tx,
            _sessions.Count,
            sessions));
    }

    public async Task<string> DiagnosticsAsync()
    {
        WriteLog("Running diagnostics…");
        var sb = new StringBuilder();
        ReconcileRemovedSessions();
        var p = FindAppleDevice(); var a = _sessions.Values.FirstOrDefault()?.Adapter ?? FindPhoneAdapter();
        sb.AppendLine($"Apple device: {(p is null ? "not connected" : p.Name)}");
        sb.AppendLine($"Apple PnP ID: {p?.Id ?? "—"}");
        sb.AppendLine($"USB identity: {UsbNative.GetDeviceId() ?? "unreachable"}");
        sb.AppendLine($"USB mode: {await UsbNative.GetModeAsync() ?? "unreachable"}");
        sb.AppendLine($"USB Ethernet: {a?.Name ?? "not present"} [{a?.OperationalStatus.ToString() ?? "—"}]");
        sb.AppendLine($"Windows USB address: {(a is null ? "—" : FindAddress(a.Name) ?? "none")}");
        sb.AppendLine($"Share mode: {_mode.DisplayName()}");
        foreach (var session in _sessions.Values)
        {
            if (session.Mode == ShareMode.ReverseTethering)
                sb.AppendLine($"ICS: {session.IcsPublicName} -> {session.Adapter.Name}");
            else
                sb.AppendLine($"USB slot: {session.HostAddress} -> {session.PeerAddress} | adapter={session.Adapter.Name} | peer={(await PingPeerAsync(session.PeerAddress, 3000) ? "reachable" : "not reachable")}");
        }
        sb.AppendLine($"Active USB sessions: {_sessions.Count}/{(_mode == ShareMode.ReverseTethering ? 1 : MaxDirectUsbDevices)}");
        sb.AppendLine(_mode == ShareMode.ReverseTethering
            ? "Network mode: reverse tethering via Windows ICS (NAT, 192.168.137.x)"
            : "Network mode: isolated static IPv4; no ICS/NAT/gateway/DNS");
        WriteLog("Diagnostics result: " + sb.ToString().Replace(Environment.NewLine, " | ").Trim());
        return sb.ToString();
    }

    private async Task BindAppleOrInboxNcmDriverAsync(UsbNative.AppleUsbTarget target)
    {
        var controlInterfaces = await UsbNative.GetNcmControlInterfacesAsync(target);
        if (controlInterfaces.Length == 0)
        {
            WriteLog("USB descriptors expose no CDC-NCM control interface after mode 5.");
            LogAppleInterfaces();
            return;
        }
        foreach (var n in controlInterfaces) WriteLog($"USB descriptor NCM control interface: {n}");

        var appleChildren = FindPnP("USB\\VID_05AC&PID_", null).Where(d => IsChildOfAppleParent(d.Id, target.ParentId)).ToList();
        var targets = controlInterfaces.Select(n => appleChildren.FirstOrDefault(d => TryGetInterfaceNumber(d.Id, out var mi) && mi == n)).Where(d => d is not null).Cast<PnpDevice>().ToList();
        // usbccgp publishes the MI_xx children a moment after the composite restarts; give it time
        // before resorting to a forced re-enumeration.
        for (var attempt = 0; attempt < 20 && targets.Count == 0; attempt++)
        {
            if (attempt == 0) WriteLog("Waiting for usbccgp to publish the NCM child PDO…");
            await Task.Delay(500);
            appleChildren = FindPnP("USB\\VID_05AC&PID_", null).Where(d => IsChildOfAppleParent(d.Id, target.ParentId)).ToList();
            targets = controlInterfaces.Select(n => appleChildren.FirstOrDefault(d => TryGetInterfaceNumber(d.Id, out var mi) && mi == n)).Where(d => d is not null).Cast<PnpDevice>().ToList();
        }
        if (targets.Count == 0)
        {
            WriteLog("NCM child PDOs are missing; forcing targeted usbccgp devnode re-enumeration before driver replacement.");
            if (ReenumerateAppleCompositeDevNode(target.ParentId, out var reenumError))
            {
                await Task.Delay(1500);
                appleChildren = FindPnP("USB\\VID_05AC&PID_", null).Where(d => IsChildOfAppleParent(d.Id, target.ParentId)).ToList();
                targets = controlInterfaces.Select(n => appleChildren.FirstOrDefault(d => TryGetInterfaceNumber(d.Id, out var mi) && mi == n)).Where(d => d is not null).Cast<PnpDevice>().ToList();
                WriteLog($"usbccgp targeted re-enumeration completed; NCM child targets now: {targets.Count}.");
            }
            else WriteLog($"usbccgp targeted re-enumeration failed, ConfigMgr error={reenumError}.");
        }
        foreach (var ncmTarget in targets)
        {
            LogPnpDriverState(ncmTarget.Id, "NCM candidate");
            LogPnpIds(target.Id);
            LogPnpUtilDrivers(target.Id);
        }
        if (targets.Count == 0)
        {
            WriteLog("CDC-NCM descriptors were present, but Windows still exposed no matching Apple MI child nodes after targeted usbccgp re-enumeration.");
            LogAppleInterfaces();
            return;
        }

        var appleInfCandidates = new[] { Path.Combine(AppDir, "AppleNcm", "AppleNcm.inf"), Path.Combine(AppDir, "Driver", "artifacts", "AppleNcm", "AppleNcm.inf"), Path.Combine(AppDir, "AppleNcm.inf") }.Where(File.Exists).ToList();
        var appleInf = appleInfCandidates.FirstOrDefault();
        WriteLog($"AppleNcm bring-up INF: {appleInf ?? "not bundled"}");
        if (appleInf is not null)
        {
            var addApple = RunAllowRestart("pnputil.exe", $"/add-driver \"{appleInf}\" /install");
            WriteLog($"AppleNcm package registration exit code: {addApple.ExitCode}");
            if (!string.IsNullOrWhiteSpace(addApple.Output)) WriteLog($"AppleNcm package output: {addApple.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(addApple.Error)) WriteLog($"AppleNcm package error: {addApple.Error.Trim()}");
            foreach (var ncmTarget in targets)
            {
                WriteLog($"Selecting bundled AppleNcm for Apple NCM interface: {ncmTarget.Id} | {ncmTarget.Name}");
                var changed = InstallSelectedDriverByDescription(ncmTarget.Id, appleInf, "Apple iPhone NCM Host Device", out var setupError);
                WriteLog($"AppleNcm SetupAPI driver selection {ncmTarget.Id}: {(changed ? "success" : "failed")}, Win32Error={setupError}");
                LogPnpDriverState(ncmTarget.Id, "after AppleNcm selection");
                LogPnpUtilDrivers(ncmTarget.Id);
                if (!changed) continue;
                await Task.Delay(2000);
                LogPnpDriverState(ncmTarget.Id, "after AppleNcm driver install settled");
                var adapter = FindPhoneAdapter(ncmTarget.ParentId);
                if (adapter?.OperationalStatus == OperationalStatus.Up) { WriteStaticLog($"AppleNcm produced a usable adapter: {adapter.Name}"); return; }
            }
            WriteLog("Bundled AppleNcm was present but did not produce an active adapter; continuing with inbox UsbNcm diagnostics.");
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidatesInf = new[] { Path.Combine(windows, "INF", "usbncm.inf"), Path.Combine(windows, "INF", "netncm.inf") }.Where(File.Exists).ToList();
        if (candidatesInf.Count == 0)
        {
            try { candidatesInf = Directory.EnumerateFiles(Path.Combine(windows, "System32", "DriverStore", "FileRepository"), "usbncm.inf", SearchOption.AllDirectories).ToList(); } catch { }
        }
        var inf = candidatesInf.FirstOrDefault();
        WriteLog($"Windows NCM INF: {inf ?? "not found"}");
        if (inf is null) { WriteLog("No Microsoft UsbNcm INF is installed on this Windows system; leaving the existing Apple NCM driver untouched."); return; }
        var add = RunAllowRestart("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"UsbNcm package registration exit code: {add.ExitCode}");
        if (!string.IsNullOrWhiteSpace(add.Output)) WriteLog($"UsbNcm package output: {add.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(add.Error)) WriteStaticLog($"UsbNcm package error: {add.Error.Trim()}");
        foreach (var ncmTarget in targets)
        {
            WriteLog($"Selecting Microsoft UsbNcm for Apple NCM interface: {ncmTarget.Id} | {ncmTarget.Name}");
            var changed = InstallSelectedNcmDriver(ncmTarget.Id, inf, out var setupError);
            WriteLog($"UsbNcm SetupAPI driver selection {ncmTarget.Id}: {(changed ? "success" : "failed")}, Win32Error={setupError}");
            LogPnpDriverState(ncmTarget.Id, "after UsbNcm selection");
            if (!changed) continue;
            await Task.Delay(2000);
            LogPnpDriverState(ncmTarget.Id, "after NCM driver install settled");
            var adapter = FindPhoneAdapter(ncmTarget.ParentId);
            if (adapter?.OperationalStatus == OperationalStatus.Up) { WriteLog($"UsbNcm produced a usable adapter: {adapter.Name}"); return; }
            WriteLog("Selected NCM function did not produce an active network adapter; trying the next descriptor-identified NCM function.");
        }
    }

    private static void LogPnpDriverState(string instanceId, string prefix)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT PNPDeviceID, Name, Service, DriverVersion, Manufacturer, ConfigManagerErrorCode, Status, PNPClass FROM Win32_PnPEntity");
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

    private bool InstallSelectedDriverByDescription(string instanceId, string infPath, string description, out uint error)
    {
        error = 0;
        var emptyGuid = Guid.Empty;
        var h = SetupDiGetClassDevs(ref emptyGuid, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (h == INVALID_HANDLE_VALUE) { error = (uint)Marshal.GetLastWin32Error(); return false; }
        try
        {
            for (uint index = 0; ; index++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(h, index, ref devInfo)) { var e = Marshal.GetLastWin32Error(); if (e == ERROR_NO_MORE_ITEMS) break; error = (uint)e; return false; }
                var id = GetDeviceInstanceId(h, ref devInfo);
                if (!string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                var installParams = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>(), DriverPath = string.Empty };
                if (!SetupDiGetDeviceInstallParams(h, ref devInfo, ref installParams)) { error = (uint)Marshal.GetLastWin32Error(); return false; }
                installParams.Flags |= DI_ENUMSINGLEINF | DI_QUIETINSTALL;
                installParams.FlagsEx |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS | DI_FLAGSEX_FILTERSIMILARDRIVERS;
                installParams.DriverPath = infPath;
                if (!SetupDiSetDeviceInstallParams(h, ref devInfo, ref installParams)) { error = (uint)Marshal.GetLastWin32Error(); return false; }
                WriteStaticLog($"SetupAPI: building restricted driver list from {infPath} for {instanceId}");
                if (!SetupDiBuildDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER)) { error = (uint)Marshal.GetLastWin32Error(); return false; }
                try
                {
                    for (uint driverIndex = 0; ; driverIndex++)
                    {
                        var driver = new SP_DRVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA>() };
                        if (!SetupDiEnumDriverInfo(h, ref devInfo, SPDIT_CLASSDRIVER, driverIndex, ref driver)) { var e = Marshal.GetLastWin32Error(); if (e == ERROR_NO_MORE_ITEMS) break; error = (uint)e; return false; }
                        WriteStaticLog($"SetupAPI AppleNcm candidate: description={driver.Description} | provider={driver.ProviderName} | version={driver.DriverVersion}");
                        if (!string.Equals(driver.Description?.Trim(), description, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!SetupDiSetSelectedDriver(h, ref devInfo, ref driver)) { error = (uint)Marshal.GetLastWin32Error(); return false; }
                        if (!DiInstallDevice(IntPtr.Zero, h, ref devInfo, ref driver, 0, out var reboot)) { error = (uint)Marshal.GetLastWin32Error(); WriteStaticLog($"SetupAPI: DiInstallDevice({description}) failed for {instanceId}, Win32Error={error}, needReboot={reboot}"); return false; }
                        WriteStaticLog($"SetupAPI: {description} installed on {instanceId}; needReboot={reboot}");
                        return true;
                    }
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

    private bool InstallSelectedNcmDriver(string instanceId, string infPath, out uint error)
    {
        error = 0;
        var emptyGuid = Guid.Empty;
        var h = SetupDiGetClassDevs(ref emptyGuid, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (h == INVALID_HANDLE_VALUE) { error = (uint)Marshal.GetLastWin32Error(); return false; }
        try
        {
            for (uint index = 0; ; index++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(h, index, ref devInfo)) { var e = Marshal.GetLastWin32Error(); if (e == ERROR_NO_MORE_ITEMS) break; error = (uint)e; return false; }
                var id = GetDeviceInstanceId(h, ref devInfo);
                if (!string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                var installParams = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>(), DriverPath = string.Empty };
                if (!SetupDiGetDeviceInstallParams(h, ref devInfo, ref installParams)) { error = (uint)Marshal.GetLastWin32Error(); WriteStaticLog($"SetupAPI: SetupDiGetDeviceInstallParams failed for {instanceId}, Win32Error={error}"); return false; }
                installParams.Flags |= DI_ENUMSINGLEINF | DI_QUIETINSTALL;
                installParams.FlagsEx |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS | DI_FLAGSEX_FILTERSIMILARDRIVERS;
                installParams.DriverPath = infPath;
                if (!SetupDiSetDeviceInstallParams(h, ref devInfo, ref installParams)) { error = (uint)Marshal.GetLastWin32Error(); WriteStaticLog($"SetupAPI: SetupDiSetDeviceInstallParams failed for {instanceId}, Win32Error={error}"); return false; }
                WriteStaticLog($"SetupAPI: building driver list restricted to {infPath} for {instanceId}");
                if (!SetupDiBuildDriverInfoList(h, ref devInfo, SPDIT_CLASSDRIVER)) { error = (uint)Marshal.GetLastWin32Error(); WriteStaticLog($"SetupAPI: restricted driver-list build failed for {instanceId}, Win32Error={error}"); return false; }
                try
                {
                    var found = false;
                    for (uint driverIndex = 0; ; driverIndex++)
                    {
                        var driver = new SP_DRVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA>() };
                        if (!SetupDiEnumDriverInfo(h, ref devInfo, SPDIT_CLASSDRIVER, driverIndex, ref driver)) { var e = Marshal.GetLastWin32Error(); if (e == ERROR_NO_MORE_ITEMS) break; error = (uint)e; return false; }
                        WriteStaticLog($"SetupAPI candidate for {instanceId}: description={driver.Description} | provider={driver.ProviderName}");
                        if (!string.Equals(driver.Description?.Trim(), "UsbNcm Host Device", StringComparison.OrdinalIgnoreCase)) continue;
                        found = true;
                        WriteStaticLog($"SetupAPI: selecting Microsoft UsbNcm driver node by description for {instanceId}");
                        if (!SetupDiSetSelectedDriver(h, ref devInfo, ref driver)) { error = (uint)Marshal.GetLastWin32Error(); WriteStaticLog($"SetupAPI: SetupDiSetSelectedDriver failed for {instanceId}, Win32Error={error}"); return false; }
                        if (!DiInstallDevice(IntPtr.Zero, h, ref devInfo, ref driver, 0, out var needReboot)) { error = (uint)Marshal.GetLastWin32Error(); WriteStaticLog($"SetupAPI: DiInstallDevice(UsbNcm) failed for {instanceId}, Win32Error={error}, needReboot={needReboot}"); return false; }
                        WriteStaticLog($"SetupAPI: Microsoft UsbNcm installed on {instanceId}; needReboot={needReboot}");
                        return true;
                    }
                    if (!found) { error = ERROR_NO_MORE_ITEMS; WriteStaticLog($"SetupAPI: {Path.GetFileName(infPath)} was not exposed as a compatible driver when the search was restricted to that INF for {instanceId}"); return false; }
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
        return SetupDiGetDeviceInstanceId(h, ref devInfo, buffer, buffer.Capacity, out _) ? buffer.ToString() : null;
    }

    private static string? GetDriverInfoDetail(IntPtr h, ref SP_DEVINFO_DATA devInfo, ref SP_DRVINFO_DATA driver, out int error)
    {
        error = 0;
        var baseSize = Marshal.SizeOf<SP_DRVINFO_DETAIL_DATA>();
        uint requiredSize = 0;
        SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, IntPtr.Zero, 0, out requiredSize);
        var firstError = Marshal.GetLastWin32Error();
        if (requiredSize < (uint)baseSize) { error = firstError; return null; }
        var bufferSize = checked((int)Math.Max(requiredSize, (uint)baseSize));
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, baseSize);
            if (!SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, buffer, (uint)bufferSize, out requiredSize)) { error = Marshal.GetLastWin32Error(); return null; }
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
    private struct SP_DEVINSTALL_PARAMS { public uint cbSize; public uint Flags; public uint FlagsEx; public IntPtr hwndParent; public IntPtr InstallMsgHandler; public IntPtr InstallMsgHandlerContext; public IntPtr FileQueue; public IntPtr ClassInstallReserved; public IntPtr Reserved; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DETAIL_DATA { public uint cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string InfFileName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SectionName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DrvDescription; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1)] public string HardwareID; }
    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DATA { public uint cbSize; public uint DriverType; public IntPtr Reserved; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ManufacturerName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName; public long DriverDate; public ulong DriverVersion; }

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DEVINSTALL_PARAMS deviceInstallParams);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiSetDeviceInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DEVINSTALL_PARAMS deviceInstallParams);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiEnumDriverInfo(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType, uint memberIndex, ref SP_DRVINFO_DATA driverInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDriverInfoDetail(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData, IntPtr driverInfoDetailData, uint driverInfoDetailDataSize, out uint requiredSize);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiSetSelectedDriverW", SetLastError = true)] private static extern bool SetupDiSetSelectedDriver(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool DiInstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_DRVINFO_DATA driverInfoData, uint flags, out bool needReboot);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDriverInfoList(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint driverType);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private const uint CR_SUCCESS = 0x00000000;
    private const uint CM_REENUMERATE_NORMAL = 0x00000000;
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")] private static extern uint CM_Get_Device_ID(uint dnDevInst, StringBuilder buffer, int bufferLen, uint ulFlags);

    private static bool ReenumerateAppleCompositeDevNode(string parentId, out uint error)
    {
        error = 0;
        try
        {
            var phone = FindAppleDevice(parentId);
            if (phone is null) { error = 1; WriteStaticLog($"ConfigMgr re-enumeration: Apple composite devnode not found for {parentId}."); return false; }
            var cr = CM_Locate_DevNodeW(out var devInst, phone.Id, 0);
            if (cr != CR_SUCCESS) { error = cr; WriteStaticLog($"ConfigMgr: CM_Locate_DevNode failed for {phone.Id}, CR=0x{cr:X8}"); return false; }
            cr = CM_Reenumerate_DevNode(devInst, CM_REENUMERATE_NORMAL);
            error = cr;
            if (cr != CR_SUCCESS) { WriteStaticLog($"ConfigMgr: CM_Reenumerate_DevNode failed for {phone.Id}, CR=0x{cr:X8}"); return false; }
            WriteStaticLog($"ConfigMgr: re-enumerated Apple composite devnode {phone.Id} successfully.");
            return true;
        }
        catch (Exception ex) { error = 1; WriteStaticLog($"ConfigMgr re-enumeration failed: {ex.GetType().Name}: {ex.Message}"); return false; }
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
                if (remaining.Length == 0) baseKey.DeleteValue("LowerFilters", false); else baseKey.SetValue("LowerFilters", remaining, RegistryValueKind.MultiString);
                WriteStaticLog($"Removed AppleLowerFilter from {pnpId}.");
            }
            WriteStaticLog($"usbccgp EnumeratorClass set to 02 00 00 on {drv}; re-enumeration will regenerate CDC compatible IDs.");
        }
        catch (Exception ex) { WriteStaticLog($"usbccgp EnumeratorClass update failed: {ex.GetType().Name}: {ex.Message}"); throw; }
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
        // MI_00 is the Apple USB control interface used by WinUSB/GetMode.
        // Never disable it even if Windows currently classifies it as WPD.
        foreach (var d in FindPnP("VID_05AC&PID_", "WPD"))
        {
            if (d.Id.Contains("&MI_00\\", StringComparison.OrdinalIgnoreCase)) continue;
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

    private static async Task ConfigureStaticNetworkAsync(string adapterName, string hostAddress)
    {
        foreach (var existing in FindAddresses(adapterName).Where(a => !a.Equals(hostAddress, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var remove = RunAllowRestart("netsh.exe", $"interface ipv4 delete address name=\\\"{adapterName}\\\" addr={existing}");
            if (remove.ExitCode != 0)
                WriteStaticLog($"Removed stale IPv4 address {existing} returned {remove.ExitCode}: {remove.Error}");
        }

        var address = RunAllowRestart("netsh.exe", $"interface ipv4 set address name=\\\"{adapterName}\\\" source=static address={hostAddress} mask=255.255.255.0 gateway=none");
        if (address.ExitCode != 0)
            throw new InvalidOperationException($"netsh IPv4 address configuration failed ({address.ExitCode}): {address.Error}");

        var dns = RunAllowRestart("netsh.exe", $"interface ipv4 set dnsservers name=\\\"{adapterName}\\\" source=static address=none");
        if (dns.ExitCode != 0)
            WriteStaticLog($"netsh DNS cleanup returned {dns.ExitCode}: {dns.Error}");
    }

    private static async Task ResetAdapterToAutomaticAsync(string adapterName)
    {
        var address = RunAllowRestart("netsh.exe", $"interface ipv4 set address name=\\\"{adapterName}\\\" source=dhcp");
        if (address.ExitCode != 0)
            WriteStaticLog($"netsh address reset to DHCP returned {address.ExitCode}: {address.Error}");
        var dns = RunAllowRestart("netsh.exe", $"interface ipv4 set dnsservers name=\\\"{adapterName}\\\" source=dhcp");
        if (dns.ExitCode != 0)
            WriteStaticLog($"netsh DNS reset to DHCP returned {dns.ExitCode}: {dns.Error}");
        await Task.Delay(1000);
    }

    private static bool IsAddressPreferred(string adapterName, string address)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapterName);
        if (nic is null) return false;
        foreach (var u in nic.GetIPProperties().UnicastAddresses)
            if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                u.Address.ToString().Equals(address, StringComparison.OrdinalIgnoreCase))
                return u.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred;
        return false;
    }

    private static bool HasAddress(string adapterName, string address) =>
        FindAddresses(adapterName).Any(a => a.Equals(address, StringComparison.OrdinalIgnoreCase));

    private static string? FindAddress(string adapterName) =>
        FindAddresses(adapterName).FirstOrDefault();

    private static IEnumerable<string> FindAddresses(string adapterName)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapterName);
        if (nic is null) yield break;
        foreach (var u in nic.GetIPProperties().UnicastAddresses)
            if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                yield return u.Address.ToString();
    }

    private static async Task<bool> PingPeerAsync(string address, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private void ReconcileRemovedSessions()
    {
        foreach (var key in _sessions.Keys.ToArray())
        {
            if (!_sessions.TryGetValue(key, out var session)) continue;
            if (FindAppleDevice(session.Target.ParentId) is not null) continue;
            _sessions.TryRemove(key, out _);
            var removed = session;
            _ = Task.Run(() => ReleaseSession(removed));   // the device is gone; ICS/DHCP cleanup must not block the UI timer
            WriteLog($"Apple USB device removed; session {key} cleaned up.");
        }
    }

    private static (double Rx, double Tx) GetRates(string adapterName)
    {
        try { var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapterName); return nic is null ? (0, 0) : (nic.GetIPv4Statistics().BytesReceived, nic.GetIPv4Statistics().BytesSent); } catch { return (0, 0); }
    }

    private static NetworkInterface? FindPhoneAdapter(string? parentId = null)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
            .ToList();
        var strong = nics.Where(n =>
            n.Description.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
            n.Description.Contains("NCM", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(parentId))
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID, NetConnectionID FROM Win32_NetworkAdapter");
                foreach (ManagementObject o in searcher.Get())
                {
                    var pnp = o["PNPDeviceID"]?.ToString() ?? "";
                    var name = o["NetConnectionID"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(pnp) ||
                        string.IsNullOrWhiteSpace(name) ||
                        !IsChildOfAppleParent(pnp, parentId))
                        continue;

                    var match = nics.FirstOrDefault(n =>
                        n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        WriteStaticLog($"Apple NCM adapter mapping: parent={parentId}, child={pnp}, adapter={match.Name}");
                        return match;
                    }
                }

                WriteStaticLog($"Apple NCM adapter mapping: no adapter child matched parent={parentId}");
            }
            catch (Exception ex)
            {
                WriteStaticLog($"Apple NCM adapter mapping failed for parent={parentId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // A target-specific lookup must never fall back to an unrelated Apple NCM
        // adapter. With multiple iPads attached, the first device can otherwise inherit
        // the second device's Ethernet adapter and both sessions end up sharing one NIC.
        if (!string.IsNullOrWhiteSpace(parentId))
            return null;

        // A target-specific lookup must never fall back to an unrelated Apple NCM
        // adapter. With multiple iPads attached, the first device can otherwise inherit
        // the second device's Ethernet adapter and both sessions end up sharing one NIC.
        if (!string.IsNullOrWhiteSpace(parentId))
            return null;

        if (strong.Count == 1) return strong[0];
        return strong.FirstOrDefault(n => HostAddresses.Any(host => HasAddress(n.Name, host)));
    }

    // Multiple Apple devices share VID_05AC&PID_12AB, so VID/PID hardware matching
    // is not sufficient to associate a network-function child with its composite parent.
    // Resolve the real Windows device-tree parent through CfgMgr32 instead.
    private static bool IsChildOfAppleParent(string childId, string parentId)
    {
        var actualParent = FindDeviceParentId(childId);
        var matched = !string.IsNullOrWhiteSpace(actualParent) &&
                      string.Equals(actualParent, parentId, StringComparison.OrdinalIgnoreCase);
        WriteStaticLog($"Apple USB network child discovery: child={childId}, expectedParent={parentId}, actualParent={actualParent ?? "none"}, matched={matched}");
        return matched;
    }

    private static string? FindDeviceParentId(string childId)
    {
        var emptyGuid = Guid.Empty;
        var h = SetupDiGetClassDevs(ref emptyGuid, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (h == INVALID_HANDLE_VALUE) return null;

        try
        {
            for (uint index = 0; ; index++)
            {
                var devInfo = new SP_DEVINFO_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
                };
                if (!SetupDiEnumDeviceInfo(h, index, ref devInfo))
                {
                    if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS) break;
                    continue;
                }

                var instanceId = GetDeviceInstanceId(h, ref devInfo);
                if (!string.Equals(instanceId, childId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (CM_Get_Parent(out var parentDevInst, devInfo.DevInst, 0) != CR_SUCCESS)
                    return null;

                var buffer = new StringBuilder(512);
                if (CM_Get_Device_ID(parentDevInst, buffer, buffer.Capacity, 0) != CR_SUCCESS)
                    return null;

                return buffer.ToString();
            }

            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(h);
        }
    }

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

    private static PnpDevice? FindAppleDevice() => FindPnP("USB\\VID_05AC&PID_", null).Where(d => !d.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
    private static PnpDevice? FindAppleDevice(string parentId) => FindPnP("USB\\VID_05AC&PID_", null).FirstOrDefault(d => d.Id.Equals(parentId, StringComparison.OrdinalIgnoreCase));

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
        foreach (var d in FindPnP("USB\\VID_05AC&PID_", null)) WriteStaticLog($"Apple USB PnP node: {d.Id} | {d.Name}");
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

    public readonly record struct SessionStatus(
        string HostAddress,
        string PeerAddress,
        string AdapterName,
        string AdapterStatus,
        string? Lease,
        double Rx,
        double Tx);

    public readonly record struct Status(
        bool AppleConnected,
        string AppleName,
        string? AdapterName,
        string AdapterStatus,
        bool Sharing,
        string? Lease,
        double Rx,
        double Tx,
        int SessionCount,
        IReadOnlyList<SessionStatus> Sessions);
    private readonly record struct CommandResult(int ExitCode, string Output, string Error);
    private sealed record PnpDevice(string Id, string Name)
    {
        public string ParentId
        {
            get
            {
                var slash = Id.LastIndexOf('\\');
                if (slash < 0) return Id;
                var token = Id[(slash + 1)..];
                var mi = token.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
                return mi < 0 ? Id : Id[..(slash + 1)] + token[..mi];
            }
        }
    }
    }