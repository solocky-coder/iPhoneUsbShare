using Microsoft.Win32;
using System.Management;

namespace iPhoneUsbShare;

// Compatibility helper for older call sites. NCM setup must not disable child PDOs
// or remove the Apple composite subtree: WPD/Apple services can hold open handles
// and ConfigMgr will legitimately veto removal. ShareEngine owns the actual
// configuration transition (registry index 2 -> restart -> SET_MODE(3) -> index 4).
internal static class NcmConfigurationRecovery
{
    internal static bool ArmDirectNcm(Action<string> log)
    {
        try
        {
            var phoneId = FindAppleCompositeId();
            if (phoneId is null)
            {
                log("NCM preparation: Apple composite parent was not found.");
                return false;
            }

            using var baseKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{phoneId}", writable: true);
            var driverKey = baseKey?.GetValue("Driver") as string;
            if (string.IsNullOrWhiteSpace(driverKey))
            {
                log("NCM preparation: Apple device has no usbccgp driver key.");
                return false;
            }

            using var usbCgpKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}", writable: true);
            if (usbCgpKey is null)
            {
                log($"NCM preparation: cannot open usbccgp software key {driverKey}.");
                return false;
            }

            usbCgpKey.SetValue(
                "EnumeratorClass",
                new byte[] { 0x02, 0x00, 0x00 },
                RegistryValueKind.Binary);

            // Do not write configuration 5/2 here. Those are USB configuration
            // values, while ShareEngine.SetConfig() deliberately uses the registry
            // indices 2 and 4 with AltConfigurationValue 0/2 respectively.
            // Do not disable MI_* children or remove the composite subtree.
            // The normal restart + SET_MODE path is the required re-enumeration.
            log($"NCM preparation: usbccgp EnumeratorClass set to 02 00 00 on {driverKey}; no child PDO removal performed.");
            return true;
        }
        catch (Exception ex)
        {
            log($"NCM preparation failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string? FindAppleCompositeId()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
        foreach (ManagementObject o in searcher.Get())
        {
            var id = o["PNPDeviceID"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id) &&
                id.Contains("USB\\VID_05AC&PID_", StringComparison.OrdinalIgnoreCase) &&
                !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
    }
}
