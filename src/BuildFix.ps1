$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -LiteralPath $path -Raw
$nl = [Environment]::NewLine
$anchor = '            // Windows 10 has no inbox UsbNcm.sys.'
if (-not $text.Contains($anchor)) { throw 'Windows 10 Ethernet comment anchor not found.' }
$marker = '            // The iPad descriptor dump shows CDC-NCM in configuration 5.'
if ($text.Contains($marker)) {
  $s = $text.IndexOf($marker); $e = $text.IndexOf($anchor, $s)
  if ($e -lt 0) { throw 'Could not locate end of previous Apple hook.' }
  $text = $text.Remove($s, $e - $s)
}
$hook = @(
'            ConfigureAppleCompositeConfiguration(5, 2);',
'            await Task.Delay(3000);',
'            InstallBundledAppleEthernetDriver();',
'            RescanAppleNetworkingInterfaces();',
'            await Task.Delay(2000);',
'',
$anchor
) -join $nl
$text = $text.Replace($anchor, $hook)
$method = @'
    private void ConfigureAppleCompositeConfiguration(uint original, uint alternate)
    {
        var parent = FindPnP("VID_05AC&PID_12AB", null).FirstOrDefault(d => !d.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
        if (parent is null) throw new InvalidOperationException("Apple composite parent devnode was not found.");
        var parentId = parent.Id;
        var parentPath = $@"SYSTEM\CurrentControlSet\Enum\{parentId}";
        using var parentKey = Registry.LocalMachine.OpenSubKey(parentPath, writable: true) ?? throw new InvalidOperationException("Cannot open Apple composite hardware registry key.");
        var paramsPath = parentPath + @"\Device Parameters";
        parentKey.SetValue("OriginalConfigurationValue", original, RegistryValueKind.DWord);
        parentKey.SetValue("AltConfigurationValue", alternate, RegistryValueKind.DWord);
        using (var parameters = Registry.LocalMachine.OpenSubKey(paramsPath, writable: true))
        {
            parameters?.SetValue("OriginalConfigurationValue", original, RegistryValueKind.DWord);
            parameters?.SetValue("AltConfigurationValue", alternate, RegistryValueKind.DWord);
        }
        var lower = parentKey.GetValue("LowerFilters") as string[];
        if (lower is not null && lower.Any(x => x.Equals("AppleLowerFilter", StringComparison.OrdinalIgnoreCase)))
        {
            var remaining = lower.Where(x => !x.Equals("AppleLowerFilter", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == 0) parentKey.DeleteValue("LowerFilters", false); else parentKey.SetValue("LowerFilters", remaining, RegistryValueKind.MultiString);
            WriteLog("Removed AppleLowerFilter from Apple composite parent.");
        }
        var drv = parentKey.GetValue("Driver") as string;
        if (!string.IsNullOrWhiteSpace(drv))
        {
            using var sw = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{drv}", writable: true);
            sw?.SetValue("EnumeratorClass", new byte[] { 0x02, 0x00, 0x00 }, RegistryValueKind.Binary);
        }
        WriteLog($"Usbccgp configuration policy: parent={parentId}, OriginalConfigurationValue={original}, AltConfigurationValue={alternate}, registry=hardware+Device Parameters");
        // CM_Disable_DevNode can be vetoed by usbccgp (CR_REMOVE_VETOED=0x17).
        // Use pnputil restart as the supported fallback instead of aborting.
        if (CM_Locate_DevNodeW(out var devInst, parentId, 0) != 0)
            throw new InvalidOperationException("Could not locate the Apple composite devnode for restart.");
        var disable = CM_Disable_DevNode(devInst, 0x00000004);
        WriteLog($"CM_Disable_DevNode(Apple composite, UI_NOT_OK) = {disable}");
        if (disable == 0)
        {
            Thread.Sleep(1500);
            var enable = CM_Enable_DevNode(devInst, 0);
            WriteLog($"CM_Enable_DevNode(Apple composite) = {enable}");
            if (enable != 0) throw new InvalidOperationException($"Could not enable the Apple composite devnode (CM error {enable}).");
        }
        else
        {
            WriteLog($"CM disable was vetoed (CR_REMOVE_VETOED={disable}); falling back to PnPUtil restart.");
            var restart = RunAllowRestart("pnputil.exe", $"/restart-device \"{parentId}\"");
            WriteLog($"Apple composite PnPUtil restart exit code: {restart.ExitCode}");
            if (!string.IsNullOrWhiteSpace(restart.Output)) WriteLog($"Apple composite restart output: {restart.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(restart.Error)) WriteLog($"Apple composite restart error: {restart.Error.Trim()}");
            if (restart.ExitCode != 0 && restart.ExitCode != 3010) throw new InvalidOperationException($"Apple composite restart failed ({restart.ExitCode}): {restart.Error}");
        }
        Thread.Sleep(3000);
    }

    private void InstallBundledAppleEthernetDriver()
    {
        var inf = Path.Combine(AppDir, "netaapl64.inf");
        var cat = Path.Combine(AppDir, "netaapl64.cat");
        var sys = Path.Combine(AppDir, "netaapl64.sys");
        if (!File.Exists(inf) || !File.Exists(cat) || !File.Exists(sys)) throw new InvalidOperationException("Bundled Apple Ethernet driver files are missing.");
        var r = RunAllowRestart("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"Apple Ethernet driver PnPUtil exit code: {r.ExitCode}");
        if (!string.IsNullOrWhiteSpace(r.Output)) WriteLog($"Apple Ethernet driver PnPUtil output: {r.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(r.Error)) WriteLog($"Apple Ethernet driver PnPUtil error: {r.Error.Trim()}");
        if (r.ExitCode != 0 && r.ExitCode != 3010) throw new InvalidOperationException($"Apple Ethernet driver installation failed ({r.ExitCode}): {r.Error}");
    }

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    private void RescanAppleNetworkingInterfaces()
    {
        try
        {
            var parent = FindPnP("VID_05AC&PID_12AB", null).FirstOrDefault(d => !d.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
            if (parent is not null && CM_Locate_DevNodeW(out var devInst, parent.Id, 0))
                WriteLog($"CM_Reenumerate_DevNode(parent, SYNCHRONOUS) = {CM_Reenumerate_DevNode(devInst, 1)}");
        }
        catch (Exception ex) { WriteLog($"CM composite re-enumeration failed: {ex.Message}"); }
        var scan = RunAllowRestart("pnputil.exe", "/scan-devices");
        WriteLog($"PNPUTIL scan exit code: {scan.ExitCode}");
        foreach (var d in FindPnP("VID_05AC&PID_12AB", null).ToList()) WriteLog($"Apple interface: {d.Id} | {d.Name}");
    }
'@
if (-not $text.Contains('private void ConfigureAppleCompositeConfiguration')) {
  $a = '    private void ConfigureUsbDevice(PnpDevice phone)'
  if (-not $text.Contains($a)) { throw 'ConfigureUsbDevice anchor not found.' }
  $text = $text.Replace($a, $method + $nl + $a)
}
if (-not $text.Contains('using System.Runtime.InteropServices;')) {
  $text = $text.Replace('using System.Net.Http;' + $nl, 'using System.Net.Http;' + $nl + 'using System.Runtime.InteropServices;' + $nl)
}
Set-Content -LiteralPath $path -Value $text -Encoding utf8 -NoNewline
Write-Host 'BuildFix completed.'
