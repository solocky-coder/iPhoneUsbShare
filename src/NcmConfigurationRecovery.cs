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

            var restart = RunPnpUtil($"/restart-device \"{phoneId}\"");
            log($"NCM configuration recovery: composite parent restart exit code={restart.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(restart.Output)) log($"NCM configuration recovery: restart output: {restart.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(restart.Error)) log($"NCM configuration recovery: restart error: {restart.Error.Trim()}");

            if (restart.Output.Contains("pending system reboot", StringComparison.OrdinalIgnoreCase) ||
                restart.Output.Contains("pending reboot", StringComparison.OrdinalIgnoreCase))
            {
                log("NCM configuration recovery: Windows reports the composite device is pending a system reboot; refusing further PnP resets in this session.");
                return false;
            }

            Thread.Sleep(1500);

            for (var i = 0; i < 30; i++)
            {
                if (FindAppleInterface(2) && FindAppleInterface(3))
                {
                    log("NCM configuration recovery: MI_02 and MI_03 are now enumerated.");
                    return true;
                }
                Thread.Sleep(500);
            }

            log("NCM configuration recovery: composite restart completed, but MI_02/MI_03 are still not visible.");
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
