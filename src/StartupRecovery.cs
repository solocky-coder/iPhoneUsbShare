using System.Diagnostics;
using System.Management;

namespace iPhoneUsbShare;

internal static class StartupRecovery
{
    private const string ApplePrefix = "USB\\VID_05AC&PID_";
    private const string Mi00Marker = "&MI_00\\";

    public static async Task RecoverWinUsbControlAsync(Action<string> log)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var parent = FindAppleCompositeParent();
            if (parent is null)
            {
                log("Startup recovery: Apple composite device is not enumerated; nothing to recover yet.");
                return;
            }

            if (FindMi00(parent) is not null)
            {
                log("Startup recovery: Apple MI_00 is already enumerated.");
                return;
            }

            log($"Startup recovery {attempt}/3: MI_00 is missing; restarting Apple composite devnode {parent}.");
            var result = RunPnpUtil($"/restart-device \"{parent}\"");
            log($"Startup recovery: pnputil /restart-device exit code {result.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(result.Output)) log($"Startup recovery: {result.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(result.Error)) log($"Startup recovery: {result.Error.Trim()}");

            await Task.Delay(1800);

            var scan = RunPnpUtil("/scan-devices");
            log($"Startup recovery: pnputil /scan-devices exit code {scan.ExitCode}.");
            await Task.Delay(1800);

            if (FindMi00(FindAppleCompositeParent() ?? parent) is not null)
            {
                log("Startup recovery: Apple MI_00 reappeared; WinUSB prerequisite check can continue.");
                return;
            }
        }

        log("Startup recovery: MI_00 is still missing after three targeted composite-device restarts.");
    }

    private static string? FindAppleCompositeParent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT PNPDeviceID, Name FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
            foreach (ManagementObject device in searcher.Get())
            {
                var id = device["PNPDeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!id.StartsWith(ApplePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;
                return id;
            }
        }
        catch { }
        return null;
    }

    private static string? FindMi00(string parentId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
            foreach (ManagementObject device in searcher.Get())
            {
                var id = device["PNPDeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!id.StartsWith(parentId + Mi00Marker[..^1], StringComparison.OrdinalIgnoreCase)) continue;
                if (id.Contains("&MI_00\\", StringComparison.OrdinalIgnoreCase)) return id;
            }
        }
        catch { }
        return null;
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
