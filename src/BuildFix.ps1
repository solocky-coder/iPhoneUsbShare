$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -LiteralPath $path -Raw
$nl = [Environment]::NewLine
$anchor = '            // Windows 10 has no inbox UsbNcm.sys.'
if (-not $text.Contains($anchor)) { throw 'Windows 10 Ethernet comment anchor not found.' }

# Replace the previous generated Apple hook, if present.
$oldStart = $text.IndexOf('    private void ConfigureAppleCompositeConfiguration')
if ($oldStart -ge 0) {
  $oldEnd = $text.IndexOf('    private void ConfigureUsbDevice', $oldStart)
  if ($oldEnd -lt 0) { throw 'Could not locate end of previous Apple hook.' }
  $text = $text.Remove($oldStart, $oldEnd - $oldStart)
}
$marker = '            // The iPad descriptor dump shows CDC-NCM in configuration 5.'
if ($text.Contains($marker)) {
  $s = $text.IndexOf($marker); $e = $text.IndexOf($anchor, $s)
  if ($e -lt 0) { throw 'Could not locate previous Apple startup hook.' }
  $text = $text.Remove($s, $e - $s)
}

# Always discover the Apple parent even when usbccgp has temporarily hidden MI_01.
$decl = '    private static PnpDevice? FindAppleDevice()'
if ($text.Contains($decl)) {
  $text = $text.Replace($decl, '    private static PnpDevice? FindAppleDeviceCore()')
  $text = $text.Replace('FindAppleDevice()', 'FindAppleDeviceRobust()')
  $text = $text.Replace('FindAppleDeviceCore()', 'FindAppleDeviceCore()')
  $needle = '    private static PnpDevice? FindAppleDeviceCore()'
  $robust = @'
    private static PnpDevice? FindAppleDeviceRobust()
    {
        var direct = FindAppleDeviceCore();
        if (direct is not null) return direct;
        var all = FindPnP("VID_05AC&PID_12AB", null).ToList();
        var parent = all.FirstOrDefault(d => !d.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
        var mi01 = all.FirstOrDefault(d => d.Id.Contains("&MI_01\\", StringComparison.OrdinalIgnoreCase));
        return parent ?? mi01 ?? all.FirstOrDefault();
    }

'@
  if (-not $text.Contains('private static PnpDevice? FindAppleDeviceRobust')) {
    $text = $text.Replace($needle, $robust + $needle)
  }
}

# Make configuration and restart operations target the composite parent, not MI_01.
$setStart = $text.IndexOf('    private static void SetConfig(string pnpId, string original, string alt)')
if ($setStart -ge 0) {
  $setEnd = $text.IndexOf('    private static void RestartDevice(string id)', $setStart)
  if ($setEnd -lt 0) { throw 'SetConfig end anchor not found.' }
  $newSet = @'
    private static string ResolveAppleCompositeId(string id)
    {
        if (!id.Contains("VID_05AC&PID_", StringComparison.OrdinalIgnoreCase)) return id;
        var pid = id.Contains("PID_12AB", StringComparison.OrdinalIgnoreCase) ? "12AB" : id.Contains("PID_12A8", StringComparison.OrdinalIgnoreCase) ? "12A8" : null;
        if (pid is null) return id;
        var parent = FindPnP($"VID_05AC&PID_{pid}", null).FirstOrDefault(d => !d.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
        return parent?.Id ?? id;
    }

    private static void SetConfig(string pnpId, string original, string alt)
    {
        var target = ResolveAppleCompositeId(pnpId);
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{target}\Device Parameters", writable: true)
            ?? throw new InvalidOperationException("Cannot open Apple USB device parameters.");
        k.SetValue("OriginalConfigurationValue", uint.Parse(original), RegistryValueKind.DWord);
        k.SetValue("AltConfigurationValue", uint.Parse(alt), RegistryValueKind.DWord);
    }

'@
  $text = $text.Remove($setStart, $setEnd - $setStart)
  $text = $text.Insert($setStart, $newSet)
}

$restartOld = 'var result = RunAllowRestart("pnputil.exe", $"/restart-device \\\"{id}\\\"");'
$restartNew = 'var result = RunAllowRestart("pnputil.exe", $"/restart-device \\\"{ResolveAppleCompositeId(id)}\\\"");'
if ($text.Contains($restartOld)) { $text = $text.Replace($restartOld, $restartNew) }

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
        using var key = Registry.LocalMachine.OpenSubKey(parentPath + @"\Device Parameters", writable: true) ?? throw new InvalidOperationException("Cannot open Apple composite Device Parameters registry key.");
        key.SetValue("OriginalConfigurationValue", original, RegistryValueKind.DWord);
        key.SetValue("AltConfigurationValue", alternate, RegistryValueKind.DWord);
        WriteLog($"Usbccgp configuration policy: parent={parentId}, OriginalConfigurationValue={original}, AltConfigurationValue={alternate}, registry=Device Parameters");
        var restart = RunAllowRestart("pnputil.exe", $"/restart-device \"{parentId}\"");
        WriteLog($"Apple composite parent restart exit code: {restart.ExitCode}");
        if (!string.IsNullOrWhiteSpace(restart.Output)) WriteLog($"Apple composite restart output: {restart.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(restart.Error)) WriteLog($"Apple composite restart error: {restart.Error.Trim()}");
        if (restart.ExitCode != 0 && restart.ExitCode != 3010) throw new InvalidOperationException($"Apple composite parent restart failed ({restart.ExitCode}): {restart.Error}");
        Thread.Sleep(2500);
        var scan = RunAllowRestart("pnputil.exe", "/scan-devices");
        WriteLog($"PNPUTIL scan after composite restart exit code: {scan.ExitCode}");
        foreach (var d in FindPnP("VID_05AC&PID_12AB", null).ToList()) WriteLog($"Apple interface: {d.Id} | {d.Name}");
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

    private void RescanAppleNetworkingInterfaces()
    {
        foreach (var d in FindPnP("VID_05AC&PID_12AB", null).ToList()) WriteLog($"Apple interface: {d.Id} | {d.Name}");
    }
'@
if (-not $text.Contains('private void ConfigureAppleCompositeConfiguration')) {
  $a = '    private void ConfigureUsbDevice(PnpDevice phone)'
  if (-not $text.Contains($a)) { throw 'ConfigureUsbDevice anchor not found.' }
  $text = $text.Replace($a, $method + $nl + $a)
}
if (-not $text.Contains('using System.Runtime.InteropServices;')) { $text = $text.Replace('using System.Net.Http;' + $nl, 'using System.Net.Http;' + $nl + 'using System.Runtime.InteropServices;' + $nl) }
Set-Content -LiteralPath $path -Value $text -Encoding utf8 -NoNewline
Write-Host 'BuildFix completed.'