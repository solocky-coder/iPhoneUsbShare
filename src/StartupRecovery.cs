using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace iPhoneUsbShare;

internal static class StartupRecovery
{
    private const string ApplePrefix = "USB\\VID_05AC&PID_";
    private const uint CrSuccess = 0;
    private const uint CrNoSuchDevNode = 0x0000000D;
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
        LogCfgMgrChildren(parent, log, "before recovery");

        if (FindMi00(parent) is not null)
        {
            log("Startup recovery: Apple MI_00 is already enumerated.");
            return;
        }

        // MI_00 is missing. A failed or interrupted NCM attempt can leave usbccgp pointed at a
        // configuration that does not enumerate (no children at all); put the safe selection back
        // before any re-enumeration so the rebuilt stack comes up with MI_00 again.
        RestoreSafeUsbccgpConfiguration(parent, log);

        // A reboot can leave usbccgp itself stuck in Code 10 while the composite
        // parent has no child PDOs. In that state CM_Reenumerate_DevNode and
        // /restart-device only restart the already-broken parent stack and do not
        // recreate MI_00. Remove only this broken Apple composite instance (and its
        // nonexistent child subtree), then let PnP scan the still-connected device
        // and rebuild usbccgp. This is deliberately restricted to Code 10 + zero
        // children at startup; normal NCM transitions never use subtree removal.
        var cmError = GetCompositeConfigManagerError(parent);
        if (cmError == 10)
        {
            log("Startup recovery: Apple composite parent is in ConfigMgr Code 10 with no child devnodes; rebuilding the usbccgp device instance once.");
            var remove = RunPnpUtil($"/remove-device \"{parent}\" /subtree");
            log($"Startup recovery: pnputil /remove-device /subtree exit code {remove.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(remove.Output)) log($"Startup recovery: {remove.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(remove.Error)) log($"Startup recovery: {remove.Error.Trim()}");
            await Task.Delay(1200);

            var scanAfterRemove = RunPnpUtil("/scan-devices");
            log($"Startup recovery: pnputil /scan-devices after Code 10 rebuild exit code {scanAfterRemove.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(scanAfterRemove.Output)) log($"Startup recovery: {scanAfterRemove.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(scanAfterRemove.Error)) log($"Startup recovery: {scanAfterRemove.Error.Trim()}");

            for (var attempt = 1; attempt <= 10; attempt++)
            {
                await Task.Delay(1000);
                parent = FindAppleCompositeParent() ?? parent;
                if (FindMi00(parent) is not null)
                {
                    LogAppleTree(parent, log, "after Code 10 rebuild");
                    LogCfgMgrChildren(parent, log, "after Code 10 rebuild");
                    log("Startup recovery: usbccgp Code 10 recovery restored Apple MI_00; WinUSB prerequisite check can continue.");
                    return;
                }
                if (attempt == 1 || attempt == 5 || attempt == 10)
                    log($"Startup recovery: waiting for MI_00 after Code 10 rebuild ({attempt}/10)…");
            }

            parent = FindAppleCompositeParent() ?? parent;
            LogAppleTree(parent, log, "after Code 10 rebuild attempt");
            LogCfgMgrChildren(parent, log, "after Code 10 rebuild attempt");
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

        log("Startup recovery: MI_00 remains absent after targeted re-enumeration and one controlled restart; no further automatic device-stack mutations will be attempted.");
        log("Startup recovery: reconnect the Apple device by USB if MI_00 remains absent.");
    }

    private static void RestoreSafeUsbccgpConfiguration(string parentId, Action<string> log)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{parentId}\Device Parameters", writable: true);
            if (key is null) return;
            var original = key.GetValue("OriginalConfigurationValue") as int?;
            var alt = key.GetValue("AltConfigurationValue") as int?;
            // Safe pair = ShareEngine.SafeIndexValue / "0" (index 2: PTP + usbmux, WinUSB on MI_00).
            if (original == 2 && alt == 0) return;
            key.SetValue("OriginalConfigurationValue", 2, RegistryValueKind.DWord);
            key.SetValue("AltConfigurationValue", 0, RegistryValueKind.DWord);
            log($"Startup recovery: usbccgp configuration selection was {original?.ToString() ?? "unset"}/{alt?.ToString() ?? "unset"}; restored the safe 2/0 before re-enumeration.");
        }
        catch (Exception ex) { log($"Startup recovery: could not restore the safe usbccgp configuration: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static int GetCompositeConfigManagerError(string parentId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_05AC&PID_%'");
            foreach (ManagementObject device in searcher.Get())
            {
                var id = device["PNPDeviceID"]?.ToString();
                if (!string.Equals(id, parentId, StringComparison.OrdinalIgnoreCase)) continue;
                return device["ConfigManagerErrorCode"] is null ? 0 : Convert.ToInt32(device["ConfigManagerErrorCode"]);
            }
        }
        catch { }
        return 0;
    }

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
            }).Where(x => !string.IsNullOrWhiteSpace(x.Id) && (string.Equals(x.Id, parentId, StringComparison.OrdinalIgnoreCase) || x.Id!.StartsWith(parentId + "&MI_", StringComparison.OrdinalIgnoreCase))).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
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
            return searcher.Get().Cast<ManagementObject>().Select(device => device["PNPDeviceID"]?.ToString()).Where(id => !string.IsNullOrWhiteSpace(id) && id.StartsWith(parentId + "&MI_", StringComparison.OrdinalIgnoreCase)).Select(id => id!).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
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

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)] private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = false)] private static extern uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = false)] private static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = false)] private static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = false)] private static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "CM_Get_Device_IDW", SetLastError = false)] private static extern uint CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, uint BufferLen, uint ulFlags);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
