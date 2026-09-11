using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

internal static class UsbNative
{
    private const string Dll = "libusb0.dll";
    private const int Vid = 0x05AC;
    private static readonly ushort[] SupportedPids = { 0x12A8, 0x12AB };

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void usb_init();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int usb_find_busses();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int usb_find_devices();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr usb_get_busses();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr usb_open(IntPtr dev);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int usb_close(IntPtr dev);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int usb_control_msg(
        IntPtr dev, int requestType, int request, int value, int index,
        [Out] byte[] bytes, int size, int timeout);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr usb_strerror();

    // x64 layout from libusb-win32's packed C structs:
    // usb_bus.devices = +528, usb_device.descriptor.idVendor = +536+8.
    private static readonly int Ptr = IntPtr.Size;
    private static readonly int BusDevicesOffset = Ptr * 2 + 512;
    private static readonly int DeviceDescriptorOffset = Ptr * 2 + 512 + Ptr;

    public static bool IsReachable()
    {
        try { return FindDevice() != IntPtr.Zero; }
        catch { return false; }
    }

    public static string? GetDeviceId()
    {
        try
        {
            var device = FindDevice(out var pid);
            return device == IntPtr.Zero ? null : $"05AC:{pid:X4}";
        }
        catch { return null; }
    }

    public static Task<string?> GetModeAsync() => Task.Run(() => GetMode());

    public static Task<bool> SetModeAsync(int mode) => Task.Run(() => SetMode(mode));

    private static string? GetMode()
    {
        var h = OpenPhone();
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new byte[4];
            var n = usb_control_msg(h, 0xC0, 0x45, 0, 0, buf, 4, 1000);
            if (n != 4) return null;
            return string.Join(":", buf);
        }
        finally { usb_close(h); }
    }

    private static bool SetMode(int mode)
    {
        var h = OpenPhone();
        if (h == IntPtr.Zero) return false;
        try
        {
            var buf = new byte[1];
            var n = usb_control_msg(h, 0xC0, 0x52, 0, mode, buf, 1, 2000);
            return n == 1 && buf[0] == 0;
        }
        finally { usb_close(h); }
    }

    private static IntPtr OpenPhone()
    {
        var dev = FindDevice();
        return dev == IntPtr.Zero ? IntPtr.Zero : usb_open(dev);
    }

    private static IntPtr FindDevice() => FindDevice(out _);

    private static IntPtr FindDevice(out ushort foundPid)
    {
        foundPid = 0;
        usb_init();
        usb_find_busses();
        usb_find_devices();

        var bus = usb_get_busses();
        while (bus != IntPtr.Zero)
        {
            var device = Marshal.ReadIntPtr(bus, BusDevicesOffset);
            while (device != IntPtr.Zero)
            {
                var descriptor = device + DeviceDescriptorOffset;
                var vid = (ushort)Marshal.ReadInt16(descriptor, 8);
                var pid = (ushort)Marshal.ReadInt16(descriptor, 10);
                if (vid == Vid && SupportedPids.Contains(pid))
                {
                    foundPid = pid;
                    return device;
                }
                device = Marshal.ReadIntPtr(device, 0);
            }
            bus = Marshal.ReadIntPtr(bus, 0);
        }
        return IntPtr.Zero;
    }
}
