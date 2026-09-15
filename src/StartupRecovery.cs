using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

internal static class StartupRecovery
{
    private const string ApplePrefix = "USB\\VID_05AC&PID_";
    private const string Mi00Marker = "&MI_00\\";
    private const uint CrSuccess = 0;
    private const uint CrNoSuchDevnode = 0x0000000D;
    private const uint CmLocateDevNodeNormal = 0x00000000;
    private const uint CmReenumerateSynchronous = 0x00000001;

    public static async Task RecoverWinUsbControlAsync(Action<string> log)
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

        log($"Startup recovery: MI_00 is missing; requesting targeted PnP re-enumeration of {parent}.");
        var reenumerate = ReenumerateDevNode(parent);
        log($"Startup recovery: CM_Reenumerate_DevNode result 0x{reenumerate:X8}.");
        await Task.Delay(1800);

        var scan = RunPnpUtil("/scan-devices");
        log($"Startup recovery: pnputil /scan-devices exit code {scan.ExitCode}.");
        if (!string.IsNullOrWhiteSpace(scan.Output)) log($"Startup recovery: {scan.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(scan.Error)) log($"Startup recovery: {scan.Error.Trim()}");
        await Task.Delay(1800);

        parent = FindAppleCompositeParent() ?? parent;
        if (FindMi00(parent) is not null)
        {
            log("Startup recovery: Apple MI_00 reappeared; WinUSB prerequisite check can continue.");
            return;
        }

        log("Startup recovery: targeted re-enumeration did not restore MI_00; no further automatic device-stack restarts will be attempted.");
        log("Startup recovery: reconnect the Apple device by USB if MI_00 remains absent.");
    }

    private static uint ReenumerateDevNode(string instanceId)
    {
        var result = CM_Locate_DevNodeW(out var devInst, instanceId, CmLocateDevNodeNormal);
        if (result != CrSuccess)
        {
            if (result == CrNoSuchDevnode)
                return result;
            return result;
        }

        return CM_Reenumerate_DevNode(devInst, CmReenumerateSynchronous);
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
                if (id.StartsWith(parentId + "&MI_00\\", StringComparison.OrdinalIgnoreCase))
                    return id;
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

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint CM_Locate_DevNodeW(
        out uint pdnDevInst,
        string pDeviceID,
        uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = false)]
    private static extern uint CM_Reenumerate_DevNode(
        uint dnDevInst,
        uint ulFlags);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
