using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

internal static class StartupRecovery
{
    private const string ApplePrefix = "USB\\VID_05AC&PID_";
    private const string AppleParentHardwareId = "USB\\VID_05AC&PID_12AB";
    private const uint CrSuccess = 0;
    private const uint CrNoSuchDevNode = 0x0000000D;
    private const uint CmLocateDevNodeNormal = 0x00000000;
    private const uint CmReenumerateSynchronous = 0x00000001;
    private const uint InstallFlagForce = 0x00000001;

    public static async Task RecoverWinUsbControlAsync(Action<string> log)
    {
        var parent = FindAppleCompositeParent();
        if (parent is null)
        {
            log("Startup recovery: Apple composite device is not enumerated; nothing to recover yet.");
            return;
        }

        LogAppleTree(parent, log, "before recovery");
        LogCfgMgrChildren(parent, log, "before recovery");

        var mi00 = FindMi00(parent);
        var parentProblem = GetConfigManagerError(parent);
        if (mi00 is not null && parentProblem is not 10)
        {
            log("Startup recovery: Apple MI_00 is already enumerated and the parent has no start failure.");
            return;
        }

        // A reboot can leave the Apple parent bound to Apple's appleusb.inf while
        // the parent is in CM_PROB_FAILED_START. Re-enumeration alone cannot repair
        // that binding. Stage and force our Usbccgp configuration package first.
        if (parentProblem == 10 || mi00 is null)
        {
            if (InstallCompositeConfiguration(parent, log))
            {
                await Task.Delay(1200);
                parent = FindAppleCompositeParent() ?? parent;
                LogAppleTree(parent, log, "after composite configuration install");
                LogCfgMgrChildren(parent, log, "after composite configuration install");
                if (FindMi00(parent) is not null)
                {
                    log("Startup recovery: Usbccgp composite configuration restored Apple child interfaces.");
                    return;
                }
            }
            else
            {
                log("Startup recovery: composite configuration installation did not complete; continuing with one PnP recovery pass.");
            }
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
        LogAppleTree(parent, log, "after re-enumeration");
        LogCfgMgrChildren(parent, log, "after re-enumeration");

        if (FindMi00(parent) is not null)
        {
            log("Startup recovery: Apple MI_00 reappeared; WinUSB prerequisite check can continue.");
            return;
        }

        log($"Startup recovery: MI_00 is still absent; requesting one controlled PnP restart of {parent}.");
        var restart = RunPnpUtil($"/restart-device \"{parent}\"");
        log($"Startup recovery: pnputil /restart-device exit code {restart.ExitCode}.");
        if (!string.IsNullOrWhiteSpace(restart.Output)) log($"Startup recovery: {restart.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(restart.Error)) log($"Startup recovery: {restart.Error.Trim()}");
        await Task.Delay(3000);

        parent = FindAppleCompositeParent() ?? parent;
        LogAppleTree(parent, log, "after controlled restart");
        LogCfgMgrChildren(parent, log, "after controlled restart");

        if (FindMi00(parent) is not null)
        {
            log("Startup recovery: controlled PnP restart restored Apple MI_00; WinUSB prerequisite check can continue.");
            return;
        }

        log("Startup recovery: MI_00 remains absent after composite configuration, targeted re-enumeration, and one controlled restart; no further automatic device-stack mutations will be attempted.");
        log("Startup recovery: reconnect the Apple device by USB if MI_00 remains absent.");
    }

    private static bool InstallCompositeConfiguration(string parentId, Action<string> log)
    {
        try
        {
            var infPath = Path.Combine(AppContext.BaseDirectory, "Driver", "AppleUsbCompositeConfiguration.inf");
            if (!File.Exists(infPath))
            {
                log($"Startup recovery: composite configuration INF not found: {infPath}");
                return false;
            }

            log($"Startup recovery: staging Apple Usbccgp configuration package: {infPath}");
            var add = RunPnpUtil($"/add-driver \"{infPath}\" /install");
            log($"Startup recovery: composite INF staging exit code {add.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(add.Output)) log($"Startup recovery: {add.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(add.Error)) log($"Startup recovery: {add.Error.Trim()}");
            if (add.ExitCode != 0 && add.ExitCode != 3010) return false;

            log($"Startup recovery: forcing composite configuration onto {AppleParentHardwareId}.");
            var forced = UpdateDriverForPlugAndPlayDevicesW(
                IntPtr.Zero,
                AppleParentHardwareId,
                infPath,
                InstallFlagForce,
                out var rebootRequired);
            if (!forced)
            {
                var error = Marshal.GetLastWin32Error();
                log($"Startup recovery: forced composite driver update failed with Win32 error {error} ({new System.ComponentModel.Win32Exception(error).Message}).");
                return false;
            }

            log($"Startup recovery: composite configuration driver update succeeded; rebootRequired={rebootRequired}.");
            var restart = RunPnpUtil($"/restart-device \"{parentId}\"");
            log($"Startup recovery: composite parent restart exit code {restart.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(restart.Output)) log($"Startup recovery: {restart.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(restart.Error)) log($"Startup recovery: {restart.Error.Trim()}");
            return restart.ExitCode == 0 || restart.ExitCode == 3010;
        }
        catch (Exception ex)
        {
            log($"Startup recovery: composite configuration install failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static int? GetConfigManagerError(string instanceId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                $"SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID='{EscapeWmi(instanceId)}'");
            var row = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (row?["ConfigManagerErrorCode"] is null) return null;
            return Convert.ToInt32(row["ConfigManagerErrorCode"]);
        }
        catch { return null; }
    }

    private static string EscapeWmi(string value) => value.Replace("\\", "\\\\").Replace("'", "''");

    private static void LogAppleTree(string parentId, Action<string> log, string phase)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT PNPDeviceID, Name, Description, ClassGuid, Status, ConfigManagerErrorCode, Manufacturer, Service FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
            var rows = searcher.Get().Cast<ManagementObject>().Select(device => new
            {
                Id = device["PNPDeviceID"]?.ToString(), Name = device["Name"]?.ToString(), Description = device["Description"]?.ToString(),
                ClassGuid = device["ClassGuid"]?.ToString(), Status = device["Status"]?.ToString(), Error = device["ConfigManagerErrorCode"]?.ToString(),
                Manufacturer = device["Manufacturer"]?.ToString(), Service = device["Service"]?.ToString()
            }).Where(x => !string.IsNullOrWhiteSpace(x.Id) &&
                (string.Equals(x.Id, parentId, StringComparison.OrdinalIgnoreCase) || IsAppleChildOfParent(x.Id!, parentId)))
              .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            log($"Startup recovery: Apple USB WMI tree {phase}: {rows.Length} device node(s).");
            foreach (var row in rows) log($"Startup recovery: WMI node={row.Id} | name={row.Name ?? "?"} | description={row.Description ?? "?"} | class={row.ClassGuid ?? "?"} | status={row.Status ?? "?"} | cm_error={row.Error ?? "?"} | manufacturer={row.Manufacturer ?? "?"} | service={row.Service ?? "?"}");
        }
        catch (Exception ex) { log($"Startup recovery: Apple USB WMI tree inspection failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void LogCfgMgrChildren(string parentId, Action<string> log, string phase)
    {
        try
        {
            var result = CM_Locate_DevNodeW(out var parentDevInst, parentId, CmLocateDevNodeNormal);
            if (result != CrSuccess) { log($"Startup recovery: ConfigMgr parent lookup failed for {parentId}, CR=0x{result:X8}."); return; }
            var children = GetCfgMgrChildren(parentDevInst);
            log($"Startup recovery: ConfigMgr USB child tree {phase}: {children.Length} child devnode(s).");
            foreach (var child in children) log($"Startup recovery: CM child={child}");
        }
        catch (Exception ex) { log($"Startup recovery: ConfigMgr child inspection failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string[] GetCfgMgrChildren(uint parentDevInst)
    {
        var ids = new List<string>();
        var result = CM_Get_Child(out var child, parentDevInst, 0);
        if (result == CrNoSuchDevNode || result != CrSuccess) return Array.Empty<string>();
        while (true)
        {
            var id = GetCfgMgrDeviceId(child);
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            result = CM_Get_Sibling(out var sibling, child, 0);
            if (result != CrSuccess) break;
            child = sibling;
        }
        return ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? GetCfgMgrDeviceId(uint devInst)
    {
        try
        {
            var result = CM_Get_Device_ID_Size(out var size, devInst, 0);
            if (result != CrSuccess) return null;
            var buffer = new char[checked((int)size + 1)];
            result = CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Length, 0);
            return result == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
        }
        catch { return null; }
    }

    private static uint ReenumerateDevNode(string instanceId)
    {
        var result = CM_Locate_DevNodeW(out var devInst, instanceId, CmLocateDevNodeNormal);
        return result == CrSuccess ? CM_Reenumerate_DevNode(devInst, CmReenumerateSynchronous) : result;
    }

    private static string? FindAppleCompositeParent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
            foreach (ManagementObject device in searcher.Get())
            {
                var id = device["PNPDeviceID"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id) && id.StartsWith(ApplePrefix, StringComparison.OrdinalIgnoreCase) && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return id;
            }
        }
        catch { }
        return null;
    }

    private static string[] FindAppleChildren(string parentId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
            return searcher.Get().Cast<ManagementObject>().Select(device => device["PNPDeviceID"]?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id) && IsAppleChildOfParent(id!, parentId))
                .Select(id => id!).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static bool IsAppleChildOfParent(string id, string parentId)
    {
        if (!id.StartsWith(ApplePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parentWithoutInstance = parentId;
        var separator = parentWithoutInstance.IndexOf('\\');
        if (separator >= 0) parentWithoutInstance = parentWithoutInstance[..separator];
        return id.StartsWith(parentWithoutInstance + "&MI_", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith(parentWithoutInstance + "&REV_", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindMi00(string parentId)
    {
        var wmi = FindAppleChildren(parentId).FirstOrDefault(id => id.Contains("&MI_00\\", StringComparison.OrdinalIgnoreCase));
        if (wmi is not null) return wmi;
        try
        {
            var result = CM_Locate_DevNodeW(out var parentDevInst, parentId, CmLocateDevNodeNormal);
            return result == CrSuccess ? GetCfgMgrChildren(parentDevInst).FirstOrDefault(id => id.Contains("&MI_00\\", StringComparison.OrdinalIgnoreCase)) : null;
        }
        catch { return null; }
    }

    private static ProcessResult RunPnpUtil(string arguments)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "pnputil.exe", Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.Start(); var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            return new ProcessResult(process.ExitCode, output, error);
        }
        catch (Exception ex) { return new ProcessResult(-1, string.Empty, ex.Message); }
    }

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool rebootRequired);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)] private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = false)] private static extern uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = false)] private static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = false)] private static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = false)] private static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "CM_Get_Device_IDW", SetLastError = false)] private static extern uint CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, uint BufferLen, uint ulFlags);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
