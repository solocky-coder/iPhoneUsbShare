using System.IO;
using System.Text;

namespace iPhoneUsbShare;

/// <summary>
/// What the app does with the USB Ethernet (CDC-NCM) link once the Apple
/// mode-switch handshake and the Windows NCM driver bind have succeeded.
/// Both modes share the identical USB bring-up; they differ only in how the
/// resulting network adapter is configured.
/// </summary>
public enum ShareMode
{
    /// <summary>
    /// Isolated point-to-point link: static IPv4 on the PC (192.168.99-102.1),
    /// a built-in DHCP server that leases the device a fixed peer address,
    /// no gateway, no DNS, and no Windows ICS/NAT. Up to four devices.
    /// </summary>
    DirectUsb,

    /// <summary>
    /// Reverse tethering: Windows Internet Connection Sharing from the PC's
    /// uplink (Wi-Fi preferred) to the device's USB Ethernet adapter, so the
    /// iPhone/iPad uses this PC's internet (192.168.137.x). One device at a time.
    /// </summary>
    ReverseTethering
}

public static class ShareModes
{
    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iPhoneUsbShare");

    private static string SettingsPath => Path.Combine(SettingsDir, "mode.txt");

    public static string DisplayName(this ShareMode mode) => mode switch
    {
        ShareMode.ReverseTethering => "Reverse tethering (Windows ICS)",
        _ => "Direct USB (isolated link)"
    };

    /// <summary>Command-line / settings token for the mode.</summary>
    public static string Token(this ShareMode mode) => mode == ShareMode.ReverseTethering ? "reverse" : "direct";

    /// <summary>
    /// Accepts: direct, direct-usb, usb, isolated / reverse, reverse-tethering, tethering, ics.
    /// Case-insensitive; '-', '_' and spaces are ignored.
    /// </summary>
    public static bool TryParse(string? text, out ShareMode mode)
    {
        mode = ShareMode.DirectUsb;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var key = new string(text.Where(c => c != '-' && c != '_' && !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        switch (key)
        {
            case "direct":
            case "directusb":
            case "usb":
            case "isolated":
                mode = ShareMode.DirectUsb;
                return true;
            case "reverse":
            case "reversetethering":
            case "tethering":
            case "ics":
                mode = ShareMode.ReverseTethering;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Last mode the user picked in the window; Direct USB when nothing was saved.</summary>
    public static ShareMode LoadSaved()
    {
        try
        {
            if (File.Exists(SettingsPath) && TryParse(File.ReadAllText(SettingsPath), out var mode)) return mode;
        }
        catch { /* unreadable settings must never block startup */ }
        return ShareMode.DirectUsb;
    }

    public static void Save(ShareMode mode)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, mode.Token(), new UTF8Encoding(false));
        }
        catch { /* best effort; the selection still applies for this run */ }
    }
}
