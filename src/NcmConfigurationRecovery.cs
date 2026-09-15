using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Text;

namespace iPhoneUsbShare;

internal static class NcmConfigurationRecovery
{
    private const string VendorProduct = "USB\\VID_05AC&PID_";
    private const uint SafeConfiguration = 2;
    private const uint NcmConfiguration = 5;

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

            parameters.SetValue("OriginalConfigurationValue", NcmConfiguration, RegistryValueKind.DWord);
            parameters.SetValue("AltConfigurationValue", SafeConfiguration, RegistryValueKind.DWord);
            log($"NCM configuration recovery: armed usbccgp for configuration {NcmConfiguration} (alternate {SafeConfiguration}) on {phoneId}.");

            var restart = Run("pnputil.exe", $"/restart-device \"{phoneId}\"");
            log($"NCM configuration recovery: composite restart exit={restart.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(restart.Output)) log($"NCM configuration recovery: {restart.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(restart.Error)) log($"NCM configuration recovery error: {restart.Error.Trim()}");
            if (restart.ExitCode != 0 && restart.ExitCode != 3010) return;

            for (var i = 0; i < 12; i++)
            {
                if (FindAppleInterface(2) && FindAppleInterface(3))
                {
                    log("NCM configuration recovery: MI_02 and MI_03 are now enumerated.");
                    return;
                }
                Thread.Sleep(500);
            }

            log("NCM configuration recovery: restart completed, but MI_02/MI_03 are not yet visible.");
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

    private static CommandResult Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CommandResult(process.ExitCode, output, error);
    }

    private readonly record struct CommandResult(int ExitCode, string Output, string Error);
}
