using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace iPhoneUsbShare;

internal static class UsbNative
{
    private const string Dll = "libusb0.dll";
    private const int Vid = 0x05AC;
    private static readonly ushort[] SupportedPids = { 0x12A8, 0x12AB };

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void usb_init();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int usb_find_busses();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int usb_find_devices();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr usb_get_busses();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr usb_open(IntPtr dev);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int usb_close(IntPtr dev);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int usb_control_msg(IntPtr dev, int requestType, int request, int value, int index, [Out] byte[] bytes, int size, int timeout);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int usb_get_descriptor(IntPtr dev, byte type, byte index, [Out] byte[] bytes, int size);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int usb_set_configuration(IntPtr dev, int configuration);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr usb_strerror();

    private static readonly int Ptr = IntPtr.Size;
    private static readonly int BusDevicesOffset = Ptr * 2 + 512;
    private static readonly int DeviceDescriptorOffset = Ptr * 3 + 512;
    private static readonly object LogLock = new();

    internal sealed record ModeDiagnostic(int BusCount, int DeviceCount, bool DeviceEnumerated, string? DeviceId, bool OpenSucceeded, int ControlReturn, int ExpectedBytes, string? Mode, string Error);

    public static bool IsReachable() { try { return FindDevice() != IntPtr.Zero; } catch { return false; } }
    public static string? GetDeviceId() { try { var device = FindDevice(out var pid); return device == IntPtr.Zero ? null : $"05AC:{pid:X4}"; } catch { return null; } }
    public static Task<string?> GetModeAsync() => Task.Run(() => { var d = GetModeDiagnostic(); if (d.Mode is null) AppendDiagnostic(d); return d.Mode; });
    public static Task<ModeDiagnostic> GetModeDiagnosticAsync() => Task.Run(GetModeDiagnostic);
    public static Task<bool> SetModeAsync(int mode) => Task.Run(() => SetMode(mode));
    public static Task<bool> SetConfigurationAsync(int configuration) => Task.Run(() => SetConfiguration(configuration));
    public static Task<int?> GetConfigurationAsync() => Task.Run(GetConfiguration);

    private static ModeDiagnostic GetModeDiagnostic()
    {
        try
        {
            usb_init();
            var buses = usb_find_busses();
            var devices = usb_find_devices();
            var dev = FindDeviceAfterEnumeration(out var pid);
            if (dev == IntPtr.Zero) return new ModeDiagnostic(buses, devices, false, null, false, 0, 4, null, $"libusb enumerated {buses} bus(es) and {devices} device(s), but no Apple 05AC device with PID 12A8/12AB was visible.");
            var id = $"05AC:{pid:X4}";
            var h = usb_open(dev);
            if (h == IntPtr.Zero) return new ModeDiagnostic(buses, devices, true, id, false, 0, 4, null, $"libusb sees {id}, but usb_open() failed: {GetUsbError()}");
            try
            {
                var buf = new byte[4];
                var n = usb_control_msg(h, 0xC0, 0x45, 0, 0, buf, 4, 1000);
                var bytes = n > 0 ? string.Join(" ", buf.Take(Math.Min(n, buf.Length)).Select(b => b.ToString("X2"))) : "";
                if (n == 3)
                {
                    var mode3 = string.Join(":", buf.Take(3));
                    if (mode3 == "5:3:3") DumpAllConfigurations(h, id);
                    return new ModeDiagnostic(buses, devices, true, id, true, n, 4, mode3, $"GET_MODE returned 3 bytes (accepted iPad form): {bytes}");
                }
                if (n != 4) return new ModeDiagnostic(buses, devices, true, id, true, n, 4, null, $"usb_open() succeeded for {id}, but GET_MODE (request 0x45) returned {n} byte(s): {bytes} ({GetUsbError()})");
                var mode = string.Join(":", buf);
                if (mode == "5:3:3:0") DumpAllConfigurations(h, id);
                return new ModeDiagnostic(buses, devices, true, id, true, n, 4, mode, $"GET_MODE succeeded: {bytes}");
            }
            finally { usb_close(h); }
        }
        catch (Exception ex) { return new ModeDiagnostic(0, 0, false, null, false, 0, 4, null, $"libusb diagnostic exception: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static bool SetConfiguration(int configuration)
    {
        var h = OpenPhone();
        if (h == IntPtr.Zero)
        {
            AppendRaw($"USB SET_CONFIGURATION({configuration}): usb_open failed: {GetUsbError()}");
            return false;
        }
        try
        {
            var result = usb_set_configuration(h, configuration);
            var current = GetConfiguration(h);
            AppendRaw($"USB SET_CONFIGURATION({configuration}): result={result}, current={(current?.ToString() ?? "unknown")}, error={(result < 0 ? GetUsbError() : "none")}");
            return result == 0;
        }
        finally { usb_close(h); }
    }

    private static int? GetConfiguration(IntPtr h)
    {
        var buf = new byte[1];
        var n = usb_control_msg(h, 0x80, 0x08, 0, 0, buf, 1, 1000);
        if (n != 1) return null;
        return buf[0];
    }

    private static int? GetConfiguration()
    {
        var h = OpenPhone();
        if (h == IntPtr.Zero) return null;
        try { return GetConfiguration(h); }
        finally { usb_close(h); }
    }

    private static void DumpAllConfigurations(IntPtr handle, string id)
    {
        try
        {
            var dev = new byte[18];
            var dn = usb_get_descriptor(handle, 0x01, 0, dev, dev.Length);
            var configCount = dn >= 18 ? dev[17] : (byte)0;
            AppendRaw($"USB MODE 5 DESCRIPTOR: {id}, deviceDescriptorReturn={dn}, bNumConfigurations={configCount}");
            for (byte index = 0; index < configCount; index++) DumpConfiguration(handle, id, index);
        }
        catch (Exception ex) { AppendRaw($"USB MODE 5 ALL-CONFIG DUMP ERROR: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void DumpConfiguration(IntPtr handle, string id, byte index)
    {
        var cfgHead = new byte[9];
        var hn = usb_get_descriptor(handle, 0x02, index, cfgHead, cfgHead.Length);
        if (hn < 9)
        {
            AppendRaw($"USB CONFIG {index + 1}: HEADER FAILED return={hn}, error={GetUsbError()}");
            return;
        }
        var total = cfgHead[2] | (cfgHead[3] << 8);
        var value = cfgHead[5];
        var cfg = new byte[Math.Clamp(total, 9, 8192)];
        var cn = usb_get_descriptor(handle, 0x02, index, cfg, cfg.Length);
        AppendRaw($"USB CONFIG {index + 1}: descriptorIndex={index}, value={value}, return={cn}, totalLength={total}, interfaces={(cn >= 5 ? cfg[4].ToString() : "?")}, raw={Hex(cfg, cn > 0 ? Math.Min(cn, cfg.Length) : 0)}");
        if (cn < 9) return;
        var pos = 0;
        while (pos + 2 <= cn)
        {
            var len = cfg[pos]; var type = cfg[pos + 1];
            if (len < 2 || pos + len > cn) break;
            if (type == 0x04 && len >= 9)
                AppendRaw($"USB CONFIG {index + 1} INTERFACE: offset={pos}, if={cfg[pos+2]}, alt={cfg[pos+3]}, eps={cfg[pos+4]}, class={cfg[pos+5]:X2}, subclass={cfg[pos+6]:X2}, protocol={cfg[pos+7]:X2}, iInterface={cfg[pos+8]}");
            else if (type == 0x05 && len >= 7)
                AppendRaw($"USB CONFIG {index + 1} ENDPOINT: offset={pos}, addr={cfg[pos+2]:X2}, attrs={cfg[pos+3]:X2}, maxPacket={(cfg[pos+4] | (cfg[pos+5] << 8))}, interval={cfg[pos+6]}");
            else if (type == 0x0B)
                AppendRaw($"USB CONFIG {index + 1} IAD: offset={pos}, raw={Hex(cfg, pos, len)}");
            else if (type == 0x24)
                AppendRaw($"USB CONFIG {index + 1} CDC EXTRA: offset={pos}, len={len}, subtype={(len >= 3 ? cfg[pos+2].ToString("X2") : "??")}, raw={Hex(cfg, pos, len)}");
            pos += len;
        }
    }

    private static string Hex(byte[] data, int count) => Hex(data, 0, count);
    private static string Hex(byte[] data, int offset, int count) => count <= 0 ? "" : string.Join(" ", data.Skip(offset).Take(count).Select(b => b.ToString("X2")));
    private static void AppendRaw(string message) { try { lock (LogLock) File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ActivityLog.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}", new UTF8Encoding(false)); } catch { } }
    private static void AppendDiagnostic(ModeDiagnostic d) => AppendRaw($"LIBUSB GET_MODE DETAIL: buses={d.BusCount}, devices={d.DeviceCount}, enumerated={d.DeviceEnumerated}, device={d.DeviceId ?? "none"}, open={d.OpenSucceeded}, return={d.ControlReturn}/{d.ExpectedBytes}, error={d.Error}");
    private static string GetUsbError() { try { var p = usb_strerror(); return p == IntPtr.Zero ? "unknown libusb error" : Marshal.PtrToStringAnsi(p) ?? "unknown libusb error"; } catch { return "libusb error text unavailable"; } }
    private static bool SetMode(int mode) { var h = OpenPhone(); if (h == IntPtr.Zero) return false; try { var buf = new byte[1]; var n = usb_control_msg(h, 0xC0, 0x52, 0, mode, buf, 1, 2000); return n == 1 && buf[0] == 0; } finally { usb_close(h); } }
    private static IntPtr OpenPhone() { var dev = FindDevice(); return dev == IntPtr.Zero ? IntPtr.Zero : usb_open(dev); }
    private static IntPtr FindDevice() => FindDevice(out _);
    private static IntPtr FindDevice(out ushort foundPid) { foundPid = 0; usb_init(); usb_find_busses(); usb_find_devices(); return FindDeviceAfterEnumeration(out foundPid); }
    private static IntPtr FindDeviceAfterEnumeration(out ushort foundPid) { foundPid = 0; var bus = usb_get_busses(); while (bus != IntPtr.Zero) { var device = Marshal.ReadIntPtr(bus, BusDevicesOffset); while (device != IntPtr.Zero) { var descriptor = device + DeviceDescriptorOffset; var vid = (ushort)Marshal.ReadInt16(descriptor, 8); var pid = (ushort)Marshal.ReadInt16(descriptor, 10); if (vid == Vid && SupportedPids.Contains(pid)) { foundPid = pid; return device; } device = Marshal.ReadIntPtr(device, 0); } bus = Marshal.ReadIntPtr(bus, 0); } return IntPtr.Zero; }
}
