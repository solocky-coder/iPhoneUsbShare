using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

internal static class NcmConfigurationRecovery
{
    private const string VendorProduct = "USB\\VID_05AC&PID_";
    private const uint SafeConfiguration = 2;
    private const uint NcmConfiguration = 5;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out IntPtr pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Disable_DevNode(IntPtr dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(IntPtr dnDevInst, uint ulFlags);

    internal static bool ArmDirectNcm(Action<string> log)
    {
        try
        {
            var phoneId = FindAppleCompositeId();
            if (phoneId is null)
            {
                log("NCM configuration recovery: Apple composite parent was not found.");
                return false;
            }

            log($"NCM configuration recovery: composite parent={phoneId}.");

            if (FindAppleInterface(2) && FindAppleInterface(3))
            {
                log("NCM configuration recovery: MI_02 and MI_03 are already enumerated; no composite restart required.");
                return true;
            }

            var compositeInf = FindCompositeInf();
            if (compositeInf is null)
            {
                log("NCM configuration recovery: Apple composite configuration INF is not bundled; cannot persist usbccgp configuration 5 selection.");
            }
            else
            {
                log($"NCM configuration recovery: installing Apple composite configuration INF: {compositeInf}");
                var add = RunPnpUtil($"/add-driver \"{compositeInf}\" /install");
                log($"NCM configuration recovery: composite INF pnputil exit code={add.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(add.Output)) log($"NCM configuration recovery: pnputil output: {add.Output.Trim()}");
                if (!string.IsNullOrWhiteSpace(add.Error)) log($"NCM configuration recovery: pnputil error: {add.Error.Trim()}");
            }

            var parametersPath = $@"SYSTEM\CurrentControlSet\Enum\{phoneId}\Device Parameters";
            using var parameters = Registry.LocalMachine.OpenSubKey(parametersPath, writable: true);
            if (parameters is null)
            {
                log($"NCM configuration recovery: cannot open {parametersPath}.");
                return false;
            }

            var oldOriginal = parameters.GetValue("OriginalConfigurationValue");
            var oldAlternate = parameters.GetValue("AltConfigurationValue");
            parameters.SetValue("OriginalConfigurationValue", NcmConfiguration, RegistryValueKind.DWord);
            parameters.SetValue("AltConfigurationValue", SafeConfiguration, RegistryValueKind.DWord);
            log($"NCM configuration recovery: usbccgp configuration selection set to Original={NcmConfiguration}, Alt={SafeConfiguration}; previous Original={oldOriginal ?? "none"}, Alt={oldAlternate ?? "none"}.");

            // pnputil /restart-device can be rejected with ERROR_OPERATION_PENDING
            // even though the devnode is perfectly recoverable. Disable/enable the
            // existing devnode directly through ConfigMgr instead of asking PnP to
            // queue another restart operation.
            var locate = CM_Locate_DevNodeW(out var devInst, phoneId, 0);
            log($"NCM configuration recovery: CM_Locate_DevNode result={locate}.");
            if (locate != 0 || devInst == IntPtr.Zero)
            {
                log("NCM configuration recovery: could not locate composite devnode through ConfigMgr.");
                return false;
            }

            var disable = CM_Disable_DevNode(devInst, 0);
            log($"NCM configuration recovery: CM_Disable_DevNode result={disable}.");
            if (disable != 0)
            {
                log("NCM configuration recovery: ConfigMgr refused to disable the composite devnode.");
                return false;
            }

            Thread.Sleep(1000);

            var enable = CM_Enable_DevNode(devInst, 0);
            log($"NCM configuration recovery: CM_Enable_DevNode result={enable}.");
            if (enable != 0)
            {
                log("NCM configuration recovery: ConfigMgr refused to enable the composite devnode.");
                return false;
            }

            for (var i = 0; i < 40; i++)
            {
                if (FindAppleInterface(2) && FindAppleInterface(3))
                {
                    log("NCM configuration recovery: MI_02 and MI_03 are now enumerated.");
                    return true;
                }
                Thread.Sleep(500);
            }

            log("NCM configuration recovery: ConfigMgr disable/enable completed, but MI_02/MI_03 are still not visible.");
            return false;
        }
        catch (Exception ex)
        {
            log($"NCM configuration recovery failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string? FindCompositeInf()
    {
        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "Driver", "AppleUsbCompositeConfiguration.inf"),
            Path.Combine(appDir, "AppleUsbCompositeConfiguration.inf"),
            Path.Combine(appDir, "Driver", "artifacts", "AppleUsbCompositeConfiguration.inf")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindAppleCompositeId()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
        foreach (ManagementObject o in searcher.Get())
        {
            var id = o["PNPDeviceID"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id) &&
                id.Contains(VendorProduct, StringComparison.OrdinalIgnoreCase) &&
                !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
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
            if ((o["PNPDeviceID"]?.ToString() ?? string.Empty).Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static ProcessResult RunPnpUtil(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
