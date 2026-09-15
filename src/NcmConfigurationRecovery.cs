using System.Management;

namespace iPhoneUsbShare;

internal static class NcmConfigurationRecovery
{
    private const int NcmConfiguration = 5;

    internal static void ArmDirectNcm(Action<string> log)
    {
        try
        {
            log("NCM activation: Apple reported direct NCM mode 5; selecting USB configuration value 5 through WinUSB.");

            var selected = UsbNative.SetConfigurationAsync(NcmConfiguration).GetAwaiter().GetResult();
            log($"NCM activation: SET_CONFIGURATION(5) result={(selected ? "accepted" : "failed")}.");
            if (!selected)
            {
                log("NCM activation: Windows did not accept USB configuration 5; leaving the existing USB stack untouched.");
                return;
            }

            for (var i = 0; i < 30; i++)
            {
                if (FindAppleInterface(2) && FindAppleInterface(3))
                {
                    log("NCM activation: Apple MI_02 and MI_03 are now enumerated.");
                    return;
                }
                Thread.Sleep(250);
            }

            log("NCM activation: USB configuration 5 was selected, but MI_02/MI_03 are not yet visible to Windows.");
        }
        catch (Exception ex)
        {
            log($"NCM activation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool FindAppleInterface(int interfaceNumber)
    {
        var marker = $"&MI_{interfaceNumber:X2}";
        using var searcher = new ManagementObjectSearcher(
            "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");

        foreach (ManagementObject o in searcher.Get())
        {
            var id = o["PNPDeviceID"]?.ToString() ?? string.Empty;
            if (id.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
