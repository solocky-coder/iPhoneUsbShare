using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace iPhoneUsbShare;

internal static class UsbNative
{
    private const int Vid = 0x05AC;
    private const string WinUsbInterfaceGuid = "{8D4D9C11-3B6B-4D3A-9B0B-7E8B2E2E0C51}";
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint DI_ENUMSINGLEINF = 0x00000002;
    private const uint DI_QUIETINSTALL = 0x00000020;
    private const uint DI_FLAGSEX_ALLOWEXCLUDEDDRVS = 0x00000001;
    private const uint SPDIT_COMPATDRIVER = 0x00000002;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const int DIGCF_DEVICEINTERFACE = 0x00000010;

    private static Guid InterfaceGuid = Guid.Parse(WinUsbInterfaceGuid);
    private static readonly object LogLock = new();
    private static readonly object InitLock = new();
    private static bool migrationAttempted;

    internal sealed record ModeDiagnostic(int BusCount, int DeviceCount, bool DeviceEnumerated, string? DeviceId, bool OpenSucceeded, int ControlReturn, int ExpectedBytes, string? Mode, string Error);
    internal sealed record AppleUsbTarget(string ParentId, string ControlInterfaceId, string? WinUsbPath)
    {
        public string DeviceKey => ParentId;
        public string Id => ParentId;
    }

    public static AppleUsbTarget[] EnumerateTargets()
    {
        EnsureWinUsbPath();
        var result = new List<AppleUsbTarget>();
        foreach (var parent in FindAppleCompositeIds())
        {
            var mi00 = FindAppleInterfaceId(parent, 0);
            if (string.IsNullOrWhiteSpace(mi00)) continue;
            var path = FindWinUsbDevicePathForInstance(mi00);
            if (path is null)
            {
                SetWinUsbDeviceParameters(mi00);
                if (InstallWinUsbDriver(mi00))
                {
                    AppendRaw($"WinUSB migration: installed WinUSB on additional Apple device {mi00}; restarting MI_00 once.");
                    RunAllowRestart("pnputil.exe", $"/restart-device \"{mi00}\"");
                }
                for (var attempt = 0; attempt < 20 && path is null; attempt++)
                {
                    Thread.Sleep(250);
                    path = FindWinUsbDevicePathForInstance(mi00);
                }
            }
            result.Add(new AppleUsbTarget(parent, mi00, path));
        }
        return result.ToArray();
    }

    public static bool IsReachable(AppleUsbTarget target)
    {
        try { return FindWinUsbDevicePathForInstance(target.ControlInterfaceId) is not null; }
        catch (Exception ex) { AppendRaw($"WINUSB reachability check failed for {target.ParentId}: {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    public static Task<string?> GetModeAsync(AppleUsbTarget target) => Task.Run(() => GetModeDiagnostic(target).Mode);
    public static Task<bool> SetModeAsync(AppleUsbTarget target, int mode) => Task.Run(() => SetMode(mode, target));
    public static Task<bool> SetConfigurationAsync(AppleUsbTarget target, int configuration) => Task.Run(() => SetConfiguration(configuration, target));
    public static Task<int?> GetConfigurationAsync(AppleUsbTarget target) => Task.Run(() => GetConfiguration(target));
    public static Task<int[]> GetNcmControlInterfacesAsync(AppleUsbTarget target) => Task.Run(() => GetNcmControlInterfaces(target));
    public static string? GetDeviceId(AppleUsbTarget target) => GetDeviceIdFromParent(target.ParentId);

    public static bool IsReachable()
    {
        try
        {
            EnsureWinUsbPath();
            return FindWinUsbDevicePath() is not null;
        }
        catch (Exception ex)
        {
            AppendRaw($"WINUSB reachability check failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static string? GetDeviceId()
    {
        try
        {            var p = FindAppleCompositeId();
            if (p is null) return null;
            var marker = "PID_";
            var i = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += marker.Length;
            var end = p.IndexOf('&', i);
            return $"05AC:{(end < 0 ? p[i..] : p[i..end])}";
        }
        catch { return null; }
    }

    public static Task<string?> GetModeAsync() => Task.Run(() => { var d = GetModeDiagnostic(); if (d.Mode is null) AppendDiagnostic(d); return d.Mode; });
    public static Task<ModeDiagnostic> GetModeDiagnosticAsync() => Task.Run(() => GetModeDiagnostic());
    public static Task<bool> SetModeAsync(int mode) => Task.Run(() => SetMode(mode));
    public static Task<bool> SetConfigurationAsync(int configuration) => Task.Run(() => SetConfiguration(configuration));
    public static Task<int?> GetConfigurationAsync() => Task.Run(() => GetConfiguration());
    public static Task<int[]> GetNcmControlInterfacesAsync() => Task.Run(() => GetNcmControlInterfaces());

    private static void EnsureWinUsbPath()
    {
        lock (InitLock)
        {
            if (!migrationAttempted) migrationAttempted = true;
            if (FindWinUsbDevicePath() is not null) return;
            var parent = FindAppleCompositeId();
            if (parent is null) return;
            RemoveLegacyLibUsbFilter(parent);
            var mi00 = FindAppleInterfaceId(parent, 0);
            if (mi00 is null)
            {
                AppendRaw("WinUSB migration: MI_00 hardware child is not currently enumerated; waiting for Apple composite re-enumeration.");
                return;
            }

            AppendRaw($"WinUSB migration: MI_00 hardware child is present: {mi00}");
            AppendRaw("WinUSB migration: the custom WinUSB device-interface GUID is not yet published; forcing driver selection on the existing MI_00 PDO.");
            SetWinUsbDeviceParameters(mi00);
            if (InstallWinUsbDriver(mi00))
            {
                AppendRaw($"WinUSB migration: installed WinUSB on {mi00}; restarting MI_00 once to publish the interface.");
                RunAllowRestart("pnputil.exe", $"/restart-device \"{mi00}\"");
            }
            RemoveLegacyLibUsbService();
        }
    }

    private static bool InstallWinUsbDriver(string instanceId)
    {
        var emptyGuid = Guid.Empty;
        var h = SetupDiGetClassDevs(ref emptyGuid, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (h == INVALID_HANDLE_VALUE) return false;
        try
        {
            var customInf = Path.Combine(AppContext.BaseDirectory, "Driver", "WinUsbControl.inf");
            if (!File.Exists(customInf))
            {
                AppendRaw($"WinUSB migration: custom control INF is missing: {customInf}");
                return false;
            }

            AppendRaw($"WinUSB migration: pre-staging custom INF: {customInf}");
            var stage = RunAllowRestart("pnputil.exe", $"/add-driver \"{customInf}\" /install");
            AppendRaw($"WinUSB migration: custom INF staging exit={stage.ExitCode}; output={stage.Output.Trim()}");
            if (stage.ExitCode != 0) return false;

            for (uint index = 0; ; index++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(h, index, ref devInfo))
                {
                    if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS) break;
                    return false;
                }
                var id = GetDeviceInstanceId(h, ref devInfo);
                if (!string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                var installParams = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>(), DriverPath = string.Empty };
                if (!SetupDiGetDeviceInstallParams(h, ref devInfo, ref installParams)) return false;
                installParams.Flags |= DI_ENUMSINGLEINF | DI_QUIETINSTALL;
                installParams.FlagsEx |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS;
                installParams.DriverPath = customInf;
                if (!SetupDiSetDeviceInstallParams(h, ref devInfo, ref installParams)) return false;
                if (!SetupDiBuildDriverInfoList(h, ref devInfo, SPDIT_COMPATDRIVER)) return false;
                try
                {
                    for (uint driverIndex = 0; ; driverIndex++)
                    {
                        var driver = new SP_DRVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA>() };
                        if (!SetupDiEnumDriverInfo(h, ref devInfo, SPDIT_COMPATDRIVER, driverIndex, ref driver))
                        {
                            if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS) break;
                            return false;
                        }
                        // The driver list is already constrained to the in-box winusb.inf by
                        // DI_ENUMSINGLEINF + DriverPath above. Do not reject the candidate just
                        // because SetupDiGetDriverInfoDetail fails to marshal its INF detail.
                        // Windows can still expose a valid WinUSB candidate while that optional
                        // detail query returns no path; the previous check left MI_00 on MTP/WPD.
                        AppendRaw($"WinUSB driver candidate: description={driver.Description} | provider={driver.ProviderName} | source=WinUsbControl.inf");
                        if (!string.Equals(driver.Description, "iPhoneUsbShare Apple USB Control Interface", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (!SetupDiSetSelectedDriver(h, ref devInfo, ref driver))
                        {
                            AppendRaw($"WinUSB driver selection failed for {instanceId}, Win32Error={Marshal.GetLastWin32Error()}");
                            return false;
                        }
                        if (!DiInstallDevice(IntPtr.Zero, h, ref devInfo, ref driver, 0, out var reboot))
                        {
                            AppendRaw($"WinUSB DiInstallDevice failed for {instanceId}, Win32Error={Marshal.GetLastWin32Error()}, reboot={reboot}");
                            return false;
                        }
                        AppendRaw($"WinUSB installed on {instanceId}; reboot={reboot}");
                        return true;
                    }
                }
                finally { SetupDiDestroyDriverInfoList(h, ref devInfo, SPDIT_COMPATDRIVER); }
                return false;            }
            return false;
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
    }

    private static string? GetDriverInfName(IntPtr h, ref SP_DEVINFO_DATA devInfo, ref SP_DRVINFO_DATA driver)
    {
        var baseSize = Marshal.SizeOf<SP_DRVINFO_DETAIL_DATA>();
        SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, IntPtr.Zero, 0, out var required);
        if (required < (uint)baseSize) return null;
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            Marshal.WriteInt32(buffer, baseSize);
            if (!SetupDiGetDriverInfoDetail(h, ref devInfo, ref driver, buffer, required, out _)) return null;
            var detail = Marshal.PtrToStructure<SP_DRVINFO_DETAIL_DATA>(buffer);
            return detail.InfFileName;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void SetWinUsbDeviceParameters(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters", writable: true);
            if (key is null) return;
            key.SetValue("DeviceInterfaceGUIDs", new[] { WinUsbInterfaceGuid }, RegistryValueKind.MultiString);
            key.SetValue("WinUsbCompatibleId", "USB\\MS_COMP_WINUSB", RegistryValueKind.String);
            AppendRaw($"WinUSB migration: registered DeviceInterfaceGUIDs and USB\\MS_COMP_WINUSB hint on {instanceId}: {WinUsbInterfaceGuid}");
        }
        catch (Exception ex) { AppendRaw($"WinUSB migration: failed to register WinUSB device parameters: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void RemoveLegacyLibUsbFilter(string parentId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{parentId}", writable: true);
            if (key is null) return;
            var filters = key.GetValue("UpperFilters") as string[];
            if (filters is null) return;
            var remaining = filters.Where(x => !x.Equals("libusb0", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == filters.Length) return;
            if (remaining.Length == 0) key.DeleteValue("UpperFilters", false);
            else key.SetValue("UpperFilters", remaining, RegistryValueKind.MultiString);
            AppendRaw($"WinUSB migration: removed libusb0 from UpperFilters on {parentId}.");
            RunAllowRestart("pnputil.exe", $"/restart-device \"{parentId}\"");
        }
        catch (Exception ex) { AppendRaw($"WinUSB migration: UpperFilters cleanup failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void RemoveLegacyLibUsbService()
    {
        try
        {
            RunAllowRestart("sc.exe", "stop libusb0");
            RunAllowRestart("sc.exe", "delete libusb0");
            var sys = Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys");
            try { if (File.Exists(sys)) File.Delete(sys); } catch { }
            AppendRaw("WinUSB migration: removed legacy libusb0 service/driver artifacts where possible.");
        }
        catch (Exception ex) { AppendRaw($"WinUSB migration: libusb0 service cleanup failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static ModeDiagnostic GetModeDiagnostic(AppleUsbTarget? target = null)
    {
        try
        {
            var path = target is null ? FindWinUsbDevicePath() : FindWinUsbDevicePathForInstance(target.ControlInterfaceId);
            if (path is null) return new ModeDiagnostic(0, 0, false, null, false, 0, 4, null, "WinUSB control interface is not present.");
            var id = target is null ? GetDeviceId() : GetDeviceId(target);

            // Use the same WinUSB open path as all target-scoped control transfers.
            // The old diagnostic path opened the interface directly and initialized
            // WinUSB itself, which can disagree with the target-scoped WinUSB handle
            // (especially after usbccgp re-enumeration). Keep one canonical open path.
            var usb = target is null ? OpenWinUsb(out var file) : OpenWinUsb(target, out file);
            if (usb == IntPtr.Zero)
                return new ModeDiagnostic(0, 0, true, id, false, 0, 4, null, $"WinUSB open/initialize failed: {Marshal.GetLastWin32Error()}");
            try
            {
                var buf = new byte[4];
                var setup = new WINUSB_SETUP_PACKET { RequestType = 0xC0, Request = 0x45, Value = 0, Index = 0, Length = 4 };
                if (!WinUsb_ControlTransfer(usb, setup, buf, (uint)buf.Length, out var transferred, IntPtr.Zero))
                    return new ModeDiagnostic(0, 0, true, id, true, 0, 4, null, $"GET_MODE failed: {Marshal.GetLastWin32Error()}");
                if (transferred == 3)
                {
                    var mode3 = string.Join(":", buf.Take(3));
                    if (mode3 == "5:3:3") DumpAllConfigurations(usb, id ?? "05AC:????");
                    return new ModeDiagnostic(0, 0, true, id, true, (int)transferred, 4, mode3, "GET_MODE returned 3 bytes via WinUSB.");
                }
                if (transferred != 4) return new ModeDiagnostic(0, 0, true, id, true, (int)transferred, 4, null, $"GET_MODE returned {transferred} bytes.");
                var mode = string.Join(":", buf);
                if (mode == "5:3:3:0") DumpAllConfigurations(usb, id ?? "05AC:????");
                return new ModeDiagnostic(0, 0, true, id, true, 4, 4, mode, "GET_MODE succeeded via WinUSB.");
            }
            finally
            {
                WinUsb_Free(usb);
                file.Dispose();
            }
        }
        catch (Exception ex) { return new ModeDiagnostic(0, 0, false, null, false, 0, 4, null, $"WinUSB diagnostic exception: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static bool SetConfiguration(int configuration, AppleUsbTarget? target = null)
    {        var usb = target is null ? OpenWinUsb(out var file) : OpenWinUsb(target, out file);
        if (usb == IntPtr.Zero) return false;
        try
        {
            var setup = new WINUSB_SETUP_PACKET { RequestType = 0x00, Request = 0x09, Value = (ushort)configuration, Index = 0, Length = 0 };
            var ok = WinUsb_ControlTransfer(usb, setup, Array.Empty<byte>(), 0, out _, IntPtr.Zero);
            AppendRaw($"USB SET_CONFIGURATION({configuration}) via WinUSB: ok={ok}, error={(ok ? "none" : Marshal.GetLastWin32Error().ToString())}");
            return ok;
        }
        finally { WinUsb_Free(usb); file.Dispose(); }
    }

    private static int[] GetNcmControlInterfaces(AppleUsbTarget? target = null)
    {
        var usb = target is null ? OpenWinUsb(out var file) : OpenWinUsb(target, out file);
        if (usb != IntPtr.Zero)
        {
            try
            {
                var result = new List<int>();
                var dev = GetDescriptor(usb, 0x01, 0, 18);
                var configCount = dev.Length >= 18 ? dev[17] : 0;
                for (byte index = 0; index < configCount; index++)
                {
                    var head = GetDescriptor(usb, 0x02, index, 9);
                    if (head.Length < 9) continue;
                    var total = head[2] | (head[3] << 8);
                    if (total < 9 || total > 8192) continue;
                    var cfg = GetDescriptor(usb, 0x02, index, total);
                    var cn = cfg.Length;
                    var pos = 0;
                    while (pos + 9 <= cn)
                    {
                        var len = cfg[pos]; var type = cfg[pos + 1];
                        if (len < 2 || pos + len > cn) break;
                        if (type == 0x04 && len >= 9 && cfg[pos + 5] == 0x02 && cfg[pos + 6] == 0x0D)
                        {
                            var number = cfg[pos + 2]; var alt = cfg[pos + 3]; var endpointCount = cfg[pos + 4]; var hasInterruptEndpoint = false;
                            var scan = pos + len; var seenEndpoints = 0;
                            while (scan + 2 <= cn && seenEndpoints < endpointCount)
                            {
                                var slen = cfg[scan]; var stype = cfg[scan + 1];
                                if (slen < 2 || scan + slen > cn) break;
                                if (stype == 0x04) break;
                                if (stype == 0x05 && slen >= 7)
                                {
                                    seenEndpoints++;
                                    var address = cfg[scan + 2]; var attributes = cfg[scan + 3];
                                    if ((attributes & 0x03) == 0x03 && (address & 0x80) != 0) hasInterruptEndpoint = true;
                                }
                                scan += slen;
                            }
                            if (alt == 0 && hasInterruptEndpoint && !result.Contains(number))
                            {
                                result.Add(number);
                                AppendRaw($"USB NCM selection: tethering control interface={number}, alt={alt}, endpoints={endpointCount}, interruptIn=true");
                            }
                            else if (alt == 0) AppendRaw($"USB NCM selection: rejected CDC-NCM control interface={number}, alt={alt}, endpoints={endpointCount}, interruptIn={hasInterruptEndpoint}");
                        }
                        pos += len;
                    }
                }
                if (result.Count > 0) return result.ToArray();
            }
            finally { WinUsb_Free(usb); file.Dispose(); }
        }
        else
        {
            file.Dispose();
        }

        var parent = target?.ParentId ?? FindAppleCompositeId();
        if (parent is null) return Array.Empty<int>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var ncm = FindAppleInterfaceId(parent, 2);
            if (ncm is not null)
            {
                AppendRaw($"USB NCM selection: discovered re-enumerated Apple CDC-NCM control PDO MI_02: {ncm}");
                return new[] { 2 };
            }
            if (attempt < 19) Thread.Sleep(250);
        }
        AppendRaw("USB NCM selection: mode-5 WinUSB handle is gone and Apple MI_02 was not published by usbccgp within 5 seconds.");
        return Array.Empty<int>();
    }

    private static int? GetConfiguration(AppleUsbTarget? target = null)
    {
        var usb = target is null ? OpenWinUsb(out var file) : OpenWinUsb(target, out file);
        if (usb == IntPtr.Zero) return null;
        try
        {
            var setup = new WINUSB_SETUP_PACKET { RequestType = 0x80, Request = 0x08, Value = 0, Index = 0, Length = 1 };
            var buf = new byte[1];
            return WinUsb_ControlTransfer(usb, setup, buf, 1, out var transferred, IntPtr.Zero) && transferred == 1 ? buf[0] : null;
        }
        finally { WinUsb_Free(usb); file.Dispose(); }
    }
    private static bool SetMode(int mode, AppleUsbTarget? target = null)
    {
        var usb = target is null ? OpenWinUsb(out var file) : OpenWinUsb(target, out file);
        if (usb == IntPtr.Zero) return false;
        try
        {
            var buf = new byte[1];
            var setup = new WINUSB_SETUP_PACKET { RequestType = 0xC0, Request = 0x52, Value = 0, Index = (ushort)mode, Length = 1 };
            var ok = WinUsb_ControlTransfer(usb, setup, buf, 1, out var transferred, IntPtr.Zero);
            AppendRaw($"USB SET_MODE({mode}) via WinUSB: ok={ok}, bytes={transferred}, result={(buf.Length > 0 ? buf[0].ToString() : "none")}");
            return ok && transferred == 1 && buf[0] == 0;
        }
        finally { WinUsb_Free(usb); file.Dispose(); }
    }

    private static byte[] GetDescriptor(IntPtr usb, byte type, byte index, int length)
    {
        var buf = new byte[length];
        var setup = new WINUSB_SETUP_PACKET { RequestType = 0x80, Request = 0x06, Value = (ushort)((type << 8) | index), Index = 0, Length = (ushort)Math.Min(length, ushort.MaxValue) };
        if (!WinUsb_ControlTransfer(usb, setup, buf, (uint)buf.Length, out var transferred, IntPtr.Zero)) return Array.Empty<byte>();
        return buf.Take((int)Math.Min(transferred, (uint)buf.Length)).ToArray();
    }

    private static void DumpAllConfigurations(IntPtr usb, string id)
    {
        try
        {
            var dev = GetDescriptor(usb, 0x01, 0, 18);
            var count = dev.Length >= 18 ? dev[17] : 0;
            AppendRaw($"USB MODE 5 DESCRIPTOR via WinUSB: {id}, bNumConfigurations={count}");
            for (byte i = 0; i < count; i++) DumpConfiguration(usb, id, i);
        }
        catch (Exception ex) { AppendRaw($"USB MODE 5 ALL-CONFIG DUMP ERROR: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void DumpConfiguration(IntPtr usb, string id, byte index)
    {
        var head = GetDescriptor(usb, 0x02, index, 9);
        if (head.Length < 9) { AppendRaw($"USB CONFIG {index + 1}: HEADER FAILED"); return; }
        var total = head[2] | (head[3] << 8); var value = head[5];
        var cfg = GetDescriptor(usb, 0x02, index, Math.Clamp(total, 9, 8192));
        AppendRaw($"USB CONFIG {index + 1}: descriptorIndex={index}, value={value}, return={cfg.Length}, totalLength={total}, interfaces={(cfg.Length >= 5 ? cfg[4].ToString() : "?")}, raw={Hex(cfg, cfg.Length)}");
        var pos = 0;
        while (pos + 2 <= cfg.Length)
        {
            var len = cfg[pos]; var type = cfg[pos + 1]; if (len < 2 || pos + len > cfg.Length) break;
            if (type == 0x04 && len >= 9) AppendRaw($"USB CONFIG {index + 1} INTERFACE: offset={pos}, if={cfg[pos+2]}, alt={cfg[pos+3]}, eps={cfg[pos+4]}, class={cfg[pos+5]:X2}, subclass={cfg[pos+6]:X2}, protocol={cfg[pos+7]:X2}, iInterface={cfg[pos+8]}");
            else if (type == 0x05 && len >= 7) AppendRaw($"USB CONFIG {index + 1} ENDPOINT: offset={pos}, addr={cfg[pos+2]:X2}, attrs={cfg[pos+3]:X2}, maxPacket={(cfg[pos+4] | (cfg[pos+5] << 8))}, interval={cfg[pos+6]}");
            else if (type == 0x0B) AppendRaw($"USB CONFIG {index + 1} IAD: offset={pos}, raw={Hex(cfg, pos, len)}");
            else if (type == 0x24) AppendRaw($"USB CONFIG {index + 1} CDC EXTRA: offset={pos}, len={len}, subtype={(len >= 3 ? cfg[pos+2].ToString("X2") : "??")}, raw={Hex(cfg, pos, len)}");
            pos += len;
        }
    }

    private static IntPtr OpenWinUsb(AppleUsbTarget target, out SafeFileHandle file)
    {
        file = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
        var path = FindWinUsbDevicePathForInstance(target.ControlInterfaceId);
        if (path is null)
        {
            AppendRaw($"WINUSB OPEN: no interface path for {target.ControlInterfaceId}");
            return IntPtr.Zero;
        }

        LogWinUsbBinding(target.ControlInterfaceId, path);
        file = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            AppendRaw($"WINUSB OPEN: CreateFile failed for {target.ControlInterfaceId}; error={error}; path={path}");
            file.Dispose();
            file = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
            return IntPtr.Zero;
        }

        if (!WinUsb_Initialize(file, out var usb))
        {
            var error = Marshal.GetLastWin32Error();
            AppendRaw($"WINUSB OPEN: WinUsb_Initialize failed for {target.ControlInterfaceId}; error={error}; path={path}");
            file.Dispose();
            file = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
            return IntPtr.Zero;
        }

        AppendRaw($"WINUSB OPEN: CreateFile + WinUsb_Initialize succeeded for {target.ControlInterfaceId}.");
        return usb;
    }

    private static IntPtr OpenWinUsb(out SafeFileHandle file)
    {
        file = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
        var path = FindWinUsbDevicePath();
        if (path is null)
        {
            AppendRaw("WINUSB OPEN: no default interface path.");
            return IntPtr.Zero;
        }

        file = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            AppendRaw($"WINUSB OPEN: default CreateFile failed; error={error}; path={path}");
            file.Dispose();
            file = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
            return IntPtr.Zero;
        }

        if (!WinUsb_Initialize(file, out var usb))
        {
            var error = Marshal.GetLastWin32Error();
            AppendRaw($"WINUSB OPEN: default WinUsb_Initialize failed; error={error}; path={path}");
            file.Dispose();
            file = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
            return IntPtr.Zero;
        }

        return usb;
    }

    private static void LogWinUsbBinding(string instanceId, string interfacePath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            var service = key?.GetValue("Service")?.ToString() ?? "<missing>";
            var driver = key?.GetValue("Driver")?.ToString() ?? "<missing>";
            var className = key?.GetValue("Class")?.ToString() ?? "<missing>";
            var description = key?.GetValue("DeviceDesc")?.ToString() ?? "<missing>";
            AppendRaw($"WINUSB BINDING: instance={instanceId} | service={service} | driver={driver} | class={className} | desc={description} | interface={interfacePath}");
        }
        catch (Exception ex)
        {
            AppendRaw($"WINUSB BINDING: registry query failed for {instanceId}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static SafeFileHandle OpenDevice(string path) => CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);

    private static string? FindWinUsbDevicePathForInstance(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        var guid = InterfaceGuid;
        var h = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, ref guid, i, ref data))
                {
                    if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS) break;
                    continue;
                }

                SetupDiGetDeviceInterfaceDetail(h, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;

                var buffer = Marshal.AllocHGlobal((int)required);
                var devInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SP_DEVINFO_DATA>());                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 5);
                    var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    Marshal.StructureToPtr(devInfo, devInfoPtr, false);
                    if (!SetupDiGetDeviceInterfaceDetail(h, ref data, buffer, required, out _, devInfoPtr))
                        continue;

                    devInfo = Marshal.PtrToStructure<SP_DEVINFO_DATA>(devInfoPtr);
                    var discoveredId = GetDeviceInstanceId(h, ref devInfo);
                    if (!discoveredId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return Marshal.PtrToStringUni(buffer + 4);
                }
                finally
                {
                    Marshal.FreeHGlobal(devInfoPtr);
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
    }

    private static string? FindWinUsbDevicePath()
    {
        var guid = InterfaceGuid;
        var h = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, ref guid, i, ref data))
                {
                    if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS) break;
                    continue;
                }
                SetupDiGetDeviceInterfaceDetail(h, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 5);
                    if (SetupDiGetDeviceInterfaceDetail(h, ref data, buffer, required, out _, IntPtr.Zero))
                        return Marshal.PtrToStringUni(buffer + 4);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
    }

    private static string? FindAppleCompositeId()
    {
        return FindAppleCompositeIds().FirstOrDefault();
    }

    private static string[] FindAppleCompositeIds()
    {
        try
        {
            var result = new List<string>();
            using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity");
            foreach (ManagementObject o in searcher.Get())
            {
                var id = o["PNPDeviceID"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id) &&
                    id.StartsWith("USB\\VID_05AC&PID_", StringComparison.OrdinalIgnoreCase) &&
                    !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                    result.Add(id);
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string? FindAppleInterfaceId(string parentId, int interfaceNumber)
    {
        var marker = $"&MI_{interfaceNumber:X2}\\";
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity");
            foreach (ManagementObject o in searcher.Get())
            {
                var id = o["PNPDeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                // Apple child instance IDs contain REV_xxxx between PID and MI.
                // Match the real child PDO identity instead of assuming that the
                // parent instance string is a literal prefix of the child ID.
                if (id.StartsWith("USB\\VID_05AC&PID_", StringComparison.OrdinalIgnoreCase) &&
                    id.Contains(marker, StringComparison.OrdinalIgnoreCase) &&
                    IsChildOfAppleParent(id, parentId))
                {
                    AppendRaw($"Apple USB child discovery: matched MI_{interfaceNumber:X2} for {parentId}: {id}");
                    return id;                }
            }
        }
        catch (Exception ex)
        {
            AppendRaw($"Apple USB child discovery: WMI lookup for MI_{interfaceNumber:X2} failed: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    private static bool IsChildOfAppleParent(string childId, string parentId)
    {
        // Multiple Apple devices share the same VID/PID hardware prefix, so
        // VID/PID matching alone is not enough to associate an MI_xx child
        // with its composite parent. Resolve the real Windows device-tree
        // parent through CfgMgr32 instead.
        var actualParent = FindDeviceParentId(childId);
        var matched = !string.IsNullOrWhiteSpace(actualParent) &&
                      string.Equals(actualParent, parentId, StringComparison.OrdinalIgnoreCase);
        AppendRaw($"Apple USB child discovery: parent match child={childId}, expectedParent={parentId}, actualParent={actualParent ?? "none"}, matched={matched}");
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
                var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(h, index, ref devInfo))
                {
                    if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS) break;
                    continue;
                }

                var instanceId = GetDeviceInstanceId(h, ref devInfo);
                if (!string.Equals(instanceId, childId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (CM_Get_Parent(out var parentDevInst, devInfo.DevInst, 0) != 0)
                    return null;

                var buffer = new StringBuilder(512);
                if (CM_Get_Device_ID(parentDevInst, buffer, buffer.Capacity, 0) != 0)
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

    private static string? GetDeviceIdFromParent(string p)
    {
        try
        {
            var marker = "PID_";
            var i = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += marker.Length;
            var end = p.IndexOf('&', i);
            return $"05AC:{(end < 0 ? p[i..] : p[i..end])}";
        }
        catch { return null; }
    }

    private static CommandResult RunAllowRestart(string file, string args)
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

    private static string GetDeviceInstanceId(IntPtr h, ref SP_DEVINFO_DATA devInfo)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(h, ref devInfo, buffer, buffer.Capacity, out _) ? buffer.ToString() : "";
    }

    private static string Hex(byte[] data, int count) => count <= 0 ? "" : string.Join(" ", data.Take(count).Select(b => b.ToString("X2")));
    private static string Hex(byte[] data, int offset, int count) => count <= 0 ? "" : string.Join(" ", data.Skip(offset).Take(count).Select(b => b.ToString("X2")));
    private static void AppendRaw(string message) { try { lock (LogLock) File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ActivityLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}", new UTF8Encoding(false)); } catch { } }
    private static void AppendDiagnostic(ModeDiagnostic d) => AppendRaw($"WINUSB GET_MODE DETAIL: device={d.DeviceId ?? "none"}, enumerated={d.DeviceEnumerated}, open={d.OpenSucceeded}, return={d.ControlReturn}/{d.ExpectedBytes}, error={d.Error}");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINUSB_SETUP_PACKET { public byte RequestType; public byte Request; public ushort Value; public ushort Index; public ushort Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINSTALL_PARAMS
    {
        public uint cbSize; public uint Flags; public uint FlagsEx; public IntPtr hwndParent; public IntPtr InstallMsgHandler; public IntPtr InstallMsgHandlerContext; public IntPtr FileQueue; public IntPtr ClassInstallReserved; public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DATA
    {
        public uint cbSize; public uint DriverType; public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
        public System.Runtime.InteropServices.ComTypes.FILETIME DriverDate; public ulong DriverVersion;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DRVINFO_DETAIL_DATA
    {
        public uint cbSize; public System.Runtime.InteropServices.ComTypes.FILETIME InfDate; public uint CompatIDsOffset; public uint CompatIDsLength; public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SectionName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string InfFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DrvDescription;
    }

    private readonly record struct CommandResult(int ExitCode, string Output, string Error);
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiGetDeviceInstallParamsW", SetLastError = true)] private static extern bool SetupDiGetDeviceInstallParams(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_DEVINSTALL_PARAMS DeviceInstallParams);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiSetDeviceInstallParamsW", SetLastError = true)] private static extern bool SetupDiSetDeviceInstallParams(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_DEVINSTALL_PARAMS DeviceInstallParams);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiBuildDriverInfoList", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType);    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiEnumDriverInfoW", SetLastError = true)] private static extern bool SetupDiEnumDriverInfo(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType, uint MemberIndex, ref SP_DRVINFO_DATA DriverInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiSetSelectedDriverW", SetLastError = true)] private static extern bool SetupDiSetSelectedDriver(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_DRVINFO_DATA DriverInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiGetDriverInfoDetailW", SetLastError = true)] private static extern bool SetupDiGetDriverInfoDetail(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_DRVINFO_DATA DriverInfoData, IntPtr DriverInfoDetailData, uint DriverInfoDetailDataSize, out uint RequiredSize);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDriverInfoList(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "DiInstallDevice", SetLastError = true)] private static extern bool DiInstallDevice(IntPtr hwndParent, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_DRVINFO_DATA DriverInfoData, uint Flags, out bool NeedReboot);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern int CM_Get_Device_ID(uint dnDevInst, StringBuilder buffer, int bufferLen, uint ulFlags);

    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, out IntPtr InterfaceHandle);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Free(IntPtr InterfaceHandle);
    [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_ControlTransfer(IntPtr InterfaceHandle, WINUSB_SETUP_PACKET SetupPacket, byte[] Buffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}