using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace iPhoneUsbShare;

internal static class NcmConfigurationRecovery
{
    private const string VendorProduct = "USB\\VID_05AC&PID_";
    private const uint SafeConfiguration = 2;
    private const uint NcmConfiguration = 5;
    private const int PnpUtilRebootRequired = 3010;

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
                log("NCM configuration recovery: MI_02 and MI_03 are already enumerated; no composite removal required.");
                return true;
            }

            var compositeInf = FindCompositeInf();
            if (compositeInf is not null)
            {
                log($"NCM configuration recovery: installing Apple composite configuration INF: {compositeInf}");
                var add = RunPnpUtil($"/add-driver \"{compositeInf}\" /install");
                log($"NCM configuration recovery: composite INF pnputil exit code={add.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(add.Output)) log($"NCM configuration recovery: pnputil output: {add.Output.Trim()}");
                if (!string.IsNullOrWhiteSpace(add.Error)) log($"NCM configuration recovery: pnputil error: {add.Error.Trim()}");
            }
            else
            {
                log("NCM configuration recovery: Apple composite configuration INF is not bundled.");
            }

            var parametersPath = $@"SYSTEM\CurrentControlSet\Enum\{phoneId}\Device Parameters";
            using (var parameters = Registry.LocalMachine.OpenSubKey(parametersPath, writable: true))
            {
                if (parameters is null)
                {
                    log($"NCM configuration recovery: cannot open {parametersPath}.");
                    return false;
                }

                var oldOriginal = parameters.GetValue("OriginalConfigurationValue");
                var oldAlternate = parameters.GetValue("AltConfigurationValue");
                parameters.SetValue("OriginalConfigurationValue", NcmConfiguration, RegistryValueKind.DWord);
                parameters.SetValue("AltConfigurationValue", SafeConfiguration, RegistryValueKind.DWord);
                parameters.SetValue("EnumeratorClass", new byte[] { 0x02, 0x00, 0x00 }, RegistryValueKind.Binary);
                log($"NCM configuration recovery: usbccgp selection set to Original={NcmConfiguration}, Alt={SafeConfiguration}; previous Original={oldOriginal ?? "none"}, Alt={oldAlternate ?? "none"}.");
                log("NCM configuration recovery: usbccgp EnumeratorClass set to 02 00 00.");
            }

            // MI_00 is the only child currently left alive. Disable it first so
            // the composite parent can be removed without the child vetoing the
            // operation. Then remove the complete usbccgp subtree and rescan.
            foreach (var child in FindAppleChildren())
            {
                if (!TryGetInterfaceNumber(child, out var mi)) continue;
                if (mi != 0) continue;

                log($"NCM configuration recovery: disabling existing Apple MI_00 child: {child}");
                var disableChild = RunPnpUtil($"/disable-device \"{child}\"");
                log($"NCM configuration recovery: MI_00 disable exit code={disableChild.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(disableChild.Output)) log($"NCM configuration recovery: MI_00 disable output: {disableChild.Output.Trim()}");
                if (!string.IsNullOrWhiteSpace(disableChild.Error)) log($"NCM configuration recovery: MI_00 disable error: {disableChild.Error.Trim()}");
                break;
            }

            log("NCM configuration recovery: removing the complete Apple composite device subtree.");
            var remove = RunPnpUtil($"/remove-device \"{phoneId}\" /subtree");
            log($"NCM configuration recovery: composite subtree removal exit code={remove.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(remove.Output)) log($"NCM configuration recovery: subtree removal output: {remove.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(remove.Error)) log($"NCM configuration recovery: subtree removal error: {remove.Error.Trim()}");

            // pnputil returns 3010 when the removal itself succeeded but Windows
            // reports that a reboot is required to finish configuration. Treat
            // this as success: the device subtree has been accepted for removal,
            // and a scan can now rebuild it using the newly selected config.
            if (remove.ExitCode == PnpUtilRebootRequired)
            {
                log("NCM configuration recovery: composite subtree removal succeeded; Windows reports reboot-required (3010). Continuing with device scan without reboot.");
            }
            else if (remove.ExitCode != 0)
            {
                log("NCM configuration recovery: Windows refused to remove the composite subtree; leaving the USB stack untouched and reporting failure.");
                return false;
            }

            log("NCM configuration recovery: scanning for USB hardware changes.");
            var scan = RunPnpUtil("/scan-devices");
            log($"NCM configuration recovery: scan-devices exit code={scan.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(scan.Output)) log($"NCM configuration recovery: scan output: {scan.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(scan.Error)) log($"NCM configuration recovery: scan error: {scan.Error.Trim()}");

            for (var i = 0; i < 40; i++)
            {
                if (FindAppleInterface(2) && FindAppleInterface(3))
                {
                    log("NCM configuration recovery: MI_02 and MI_03 are now enumerated.");
                    return true;
                }
                Thread.Sleep(500);
            }

            log("NCM configuration recovery: composite subtree was rebuilt, but MI_02/MI_03 are still not visible.");
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

    private static IEnumerable<string> FindAppleChildren()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
        foreach (ManagementObject o in searcher.Get())
        {
            var id = o["PNPDeviceID"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id) && id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                yield return id;
        }
    }

    private static bool FindAppleInterface(int interfaceNumber)
    {
        var marker = $"&MI_{interfaceNumber:X2}";
        return FindAppleChildren().Any(id => id.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetInterfaceNumber(string instanceId, out int interfaceNumber)
    {
        interfaceNumber = -1;
        var marker = instanceId.LastIndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || marker + 5 > instanceId.Length) return false;
        return int.TryParse(instanceId.AsSpan(marker + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out interfaceNumber);
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
