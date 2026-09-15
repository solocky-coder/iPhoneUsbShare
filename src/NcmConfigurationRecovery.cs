using Microsoft.Win32;
using System.Management;
using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

internal static class NcmConfigurationRecovery
{
    private const string VendorProduct = "USB\\VID_05AC&PID_";
    private const uint SafeConfiguration = 2;
    private const uint NcmConfiguration = 5;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    private const uint CR_SUCCESS = 0x00000000;
    private const uint CM_DISABLE_UI_NOT_OK = 0x00000000;

    internal static void ArmDirectNcm(Action<string> log)
    {
        try
        {
            var phoneId = FindAppleCompositeId();
            if (phoneId is null)
            {
                log("NCM configuration recovery: Apple composite parent was not found.");
                return;
            }

            var parametersPath = $@"SYSTEM\CurrentControlSet\Enum\{phoneId}\Device Parameters";
            using var parameters = Registry.LocalMachine.OpenSubKey(parametersPath, writable: true);
            if (parameters is null)
            {
                log($"NCM configuration recovery: cannot open {parametersPath}.");
                return;
            }

            var oldOriginal = parameters.GetValue("OriginalConfigurationValue");
            var oldAlternate = parameters.GetValue("AltConfigurationValue");
            parameters.SetValue("OriginalConfigurationValue", NcmConfiguration, RegistryValueKind.DWord);
            parameters.SetValue("AltConfigurationValue", SafeConfiguration, RegistryValueKind.DWord);
            log($"NCM configuration recovery: armed usbccgp for configuration {NcmConfiguration} (alternate {SafeConfiguration}) on {phoneId}; previous Original={oldOriginal ?? "none"}, Alt={oldAlternate ?? "none"}.");

            if (!LocateDevNode(phoneId, out var devInst, out var locateCr))
            {
                log($"NCM configuration recovery: CM_Locate_DevNode failed, ConfigMgr error={locateCr:X8}.");
                return;
            }

            // Do not use pnputil /restart-device here. A prior driver install can leave
            // pnputil reporting "pending system reboot" even though ConfigMgr can still
            // disable/enable the composite devnode immediately. usbccgp must rebuild its
            // child PDOs after the configuration-selection registry values are changed.
            var disableCr = CM_Disable_DevNode(devInst, CM_DISABLE_UI_NOT_OK);
            log($"NCM configuration recovery: CM_Disable_DevNode result={disableCr:X8}.");
            if (disableCr != CR_SUCCESS) return;

            Thread.Sleep(500);

            var enableCr = CM_Enable_DevNode(devInst, 0);
            log($"NCM configuration recovery: CM_Enable_DevNode result={enableCr:X8}.");
            if (enableCr != CR_SUCCESS) return;

            for (var i = 0; i < 24; i++)
            {
                if (FindAppleInterface(2) && FindAppleInterface(3))
                {
                    log("NCM configuration recovery: MI_02 and MI_03 are now enumerated.");
                    return;
                }
                Thread.Sleep(500);
            }

            log("NCM configuration recovery: ConfigMgr disable/enable completed, but MI_02/MI_03 are still not visible.");
        }
        catch (Exception ex)
        {
            log($"NCM configuration recovery failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? FindAppleCompositeId()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
        foreach (ManagementObject o in searcher.Get())
        {
            var id = o["PNPDeviceID"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id) && id.Contains(VendorProduct, StringComparison.OrdinalIgnoreCase) && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
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

    private static bool LocateDevNode(string instanceId, out uint devInst, out uint cr)
    {
        devInst = 0;
        cr = CM_Locate_DevNodeW(out devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
        return cr == CR_SUCCESS;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);
}
