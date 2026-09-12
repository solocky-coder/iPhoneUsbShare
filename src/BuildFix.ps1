$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -LiteralPath $path -Raw
$nl = [Environment]::NewLine
$anchor = '            // Windows 10 has no inbox UsbNcm.sys.'
if (-not $text.Contains($anchor)) { throw 'Windows 10 Ethernet comment anchor not found.' }

# Remove any previously injected Apple startup hook and helper methods.
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

# Restore the known-good filter behavior: install libusb-win32 on the Apple
# composite VID/PID only. Do NOT install a second filter on MI_01; doing so
# can prevent libusb from enumerating the composite device at all.
# Keep discovery broad enough to find the composite parent or an interface.
$decl = '    private static PnpDevice? FindAppleDevice()'
if ($text.Contains($decl)) {
  $text = $text.Replace($decl, '    private static PnpDevice? FindAppleDeviceCore()')
  $text = $text.Replace('FindAppleDevice()', 'FindAppleDeviceRobust()')
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

# Configuration policy belongs on the composite parent's Device Parameters key.
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

# Restart the composite parent, never the MI_01 child.
$restartOld = 'var result = RunAllowRestart("pnputil.exe", $"/restart-device \\\"{id}\\\"");'
$restartNew = 'var result = RunAllowRestart("pnputil.exe", $"/restart-device \\\"{ResolveAppleCompositeId(id)}\\\"");'
if ($text.Contains($restartOld)) { $text = $text.Replace($restartOld, $restartNew) }

Set-Content -LiteralPath $path -Value $text -Encoding utf8 -NoNewline
Write-Host 'BuildFix completed.'