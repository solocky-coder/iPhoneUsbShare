$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -LiteralPath $path -Raw
$nl = [Environment]::NewLine

$anchor = '            // Windows 10 has no inbox UsbNcm.sys.'
if (-not $text.Contains($anchor)) { throw 'Windows 10 Ethernet comment anchor not found.' }

$marker = '            // The iPad descriptor dump shows CDC-NCM in configuration 5.'
if ($text.Contains($marker)) {
  $s = $text.IndexOf($marker)
  $e = $text.IndexOf($anchor, $s)
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
        var parent = FindPnP("VID_05AC&PID_12AB", null)
            .FirstOrDefault(d => !d.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
        if (parent is null)
            throw new InvalidOperationException("Apple composite parent devnode was not found.");

        var parentId = parent.Id;
        var parentPath = $@"SYSTEM\CurrentControlSet\Enum\{parentId}";
        using var parentKey = Registry.LocalMachine.OpenSubKey(parentPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Apple composite hardware registry key.");

        var paramsPath = parentPath + @"\Device Parameters";
        parentKey.SetValue("OriginalConfigurationValue", original, RegistryValueKind.DWord);
        parentKey.SetValue("AltConfigurationValue", alternate, RegistryValueKind.DWord);

        using (var parameters = Registry.LocalMachine.OpenSubKey(paramsPath, writable: true))
        {
            parameters?.SetValue("OriginalConfigurationValue", original, RegistryValueKind.DWord);
            parameters?.SetValue("AltConfigurationValue", alternate, RegistryValueKind.DWord);
        }

        var lower = parentKey.GetValue("LowerFilters") as string[];
        if (lower is not null &&
            lower.Any(x => x.Equals("AppleLowerFilter", StringComparison.OrdinalIgnoreCase)))
        {
            var remaining = lower
                .Where(x => !x.Equals("AppleLowerFilter", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (remaining.Length == 0)
                parentKey.DeleteValue("LowerFilters", false);
            else
                parentKey.SetValue("LowerFilters", remaining, RegistryValueKind.MultiString);

            WriteLog("Removed AppleLowerFilter from Apple composite parent.");
        }

        var drv = parentKey.GetValue("Driver") as string;
        if (!string.IsNullOrWhiteSpace(drv))
        {
            using var sw = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Class\{drv}", writable: true);
            sw?.SetValue("EnumeratorClass",
                new byte[] { 0x02, 0x00, 0x00 },
                RegistryValueKind.Binary);
        }

        WriteLog($"Usbccgp configuration policy: parent={parentId}, OriginalConfigurationValue={original}, AltConfigurationValue={alternate}, registry=hardware+Device Parameters");

        if (CM_Locate_DevNodeW(out var devInst, parentId, 0) != 0)
            throw new InvalidOperationException("Could not locate the Apple composite devnode.");

        var remove = RunAllowRestart(
            "pnputil.exe",
            $"/remove-device \"{parentId}\" /subtree");

        WriteLog($"Apple composite subtree removal exit code: {remove.ExitCode}");
        if (!string.IsNullOrWhiteSpace(remove.Output))
            WriteLog($"Apple composite removal output: {remove.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(remove.Error))
            WriteLog($"Apple composite removal error: {remove.Error.Trim()}");

        if (remove.ExitCode != 0 && remove.ExitCode != 3010)
            throw new InvalidOperationException(
                $"Apple composite subtree removal failed ({remove.ExitCode}): {remove.Error}");

        Thread.Sleep(2000);

        var scan = RunAllowRestart("pnputil.exe", "/scan-devices");
        WriteLog($"PNPUTIL scan after composite removal exit code: {scan.ExitCode}");
        Thread.Sleep(3000);
    }

    private void InstallBundledAppleEthernetDriver()
    {
        var inf = Path.Combine(AppDir, "netaapl64.inf");
        var cat = Path.Combine(AppDir, "netaapl64.cat");
        var sys = Path.Combine(AppDir, "netaapl64.sys");

        if (!File.Exists(inf) || !File.Exists(cat) || !File.Exists(sys))
            throw new InvalidOperationException("Bundled Apple Ethernet driver files are missing.");

        var r = RunAllowRestart("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"Apple Ethernet driver PnPUtil exit code: {r.ExitCode}");
        if (!string.IsNullOrWhiteSpace(r.Output))
            WriteLog($"Apple Ethernet driver PnPUtil output: {r.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(r.Error))
            WriteLog($"Apple Ethernet driver PnPUtil error: {r.Error.Trim()}");

        if (r.ExitCode != 0 && r.ExitCode != 3010)
            throw new InvalidOperationException(
                $"Apple Ethernet driver installation failed ({r.ExitCode}): {r.Error}");
    }

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(
        out uint pdnDevInst, string pDeviceID, uint ulFlags);

    private void RescanAppleNetworkingInterfaces()
    {
        try
        {
            foreach (var d in FindPnP("VID_05AC&PID_12AB", null).ToList())
                WriteLog($"Apple interface: {d.Id} | {d.Name}");
        }
        catch (Exception ex)
        {
            WriteLog($"Apple networking interface scan failed: {ex.Message}");
        }
    }
'@

if (-not $text.Contains('private void ConfigureAppleCompositeConfiguration')) {
  $a = '    private void ConfigureUsbDevice(PnpDevice phone)'
  if (-not $text.Contains($a)) { throw 'ConfigureUsbDevice anchor not found.' }
  $text = $text.Replace($a, $method + $nl + $a)
}

if (-not $text.Contains('using System.Runtime.InteropServices;')) {
  $text = $text.Replace(
    'using System.Net.Http;' + $nl,
    'using System.Net.Http;' + $nl +
    'using System.Runtime.InteropServices;' + $nl)
}

Set-Content -LiteralPath $path -Value $text -Encoding utf8 -NoNewline
Write-Host 'BuildFix completed.'
