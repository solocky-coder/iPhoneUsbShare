using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

internal static class StartupRecovery
{
    private const string ApplePrefix = "USB\\VID_05AC&PID_";
    private const uint CrSuccess = 0;
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

        LogAppleTree(parent, log, "before recovery");

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
        LogAppleTree(parent, log, "after recovery");

        if (FindMi00(parent) is not null)
        {
            log("Startup recovery: Apple MI_00 reappeared; WinUSB prerequisite check can continue.");
            return;
        }

        log("Startup recovery: targeted re-enumeration did not restore MI_00; no further automatic device-stack restarts will be attempted.");
        log("Startup recovery: reconnect the Apple device by USB if MI_00 remains absent.");
    }

    private static void LogAppleTree(string parentId, Action<string> log, string phase)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT PNPDeviceID, Name, Description, ClassGuid, Status, ConfigManagerErrorCode, Manufacturer, Service FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");

            var rows = searcher.Get()
                .Cast<ManagementObject>()
                .Select(device => new
                {
                    Id = device["PNPDeviceID"]?.ToString(),
                    Name = device["Name"]?.ToString(),
                    Description = device["Description"]?.ToString(),
                    ClassGuid = device["ClassGuid"]?.ToString(),
                    Status = device["Status"]?.ToString(),
                    Error = device["ConfigManagerErrorCode"]?.ToString(),
                    Manufacturer = device["Manufacturer"]?.ToString(),
                    Service = device["Service"]?.ToString()
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) &&
                            (string.Equals(x.Id, parentId, StringComparison.OrdinalIgnoreCase) ||
                             x.Id!.StartsWith(parentId + "&MI_", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            log($"Startup recovery: Apple USB tree {phase}: {rows.Length} device node(s).");
            foreach (var row in rows)
            {
                log($"Startup recovery: node={row.Id} | name={row.Name ?? "?"} | description={row.Description ?? "?"} | class={row.ClassGuid ?? "?"} | status={row.Status ?? "?"} | cm_error={row.Error ?? "?"} | manufacturer={row.Manufacturer ?? "?"} | service={row.Service ?? "?"}");
            }

            if (rows.Length == 1)
                log("Startup recovery: Apple parent has no MI_* child PDOs visible to Win32_PnPEntity; this points to USB configuration/firmware enumeration rather than a missing child driver.");
        }
        catch (Exception ex)
        {
            log($"Startup recovery: Apple USB tree inspection failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static uint ReenumerateDevNode(string instanceId)
    {
        var result = CM_Locate_DevNodeW(out var devInst, instanceId, CmLocateDevNodeNormal);
        if (result != CrSuccess)
            return result;

        return CM_Reenumerate_DevNode(devInst, CmReenumerateSynchronous);
    }

    private static string? FindAppleCompositeParent()
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
                if (!id.StartsWith(ApplePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;
                return id;
            }
        }
        catch { }
        return null;
    }

    private static string[] FindAppleChildren(string parentId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
            return searcher.Get()
                .Cast<ManagementObject>()
                .Select(device => device["PNPDeviceID"]?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id) &&
                             id.StartsWith(parentId + "&MI_", StringComparison.OrdinalIgnoreCase))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string? FindMi00(string parentId)
    {
        return FindAppleChildren(parentId)
            .FirstOrDefault(id => id.Contains("&MI_00\\", StringComparison.OrdinalIgnoreCase));
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
