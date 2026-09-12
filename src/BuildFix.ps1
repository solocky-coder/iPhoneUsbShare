$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -LiteralPath $path -Raw
$nl = [Environment]::NewLine
$anchor = '            // Windows 10 has no inbox UsbNcm.sys.'
if (-not $text.Contains($anchor)) { throw 'Windows 10 Ethernet comment anchor not found.' }

$oldBlock = @'
            // Windows 10 has no inbox UsbNcm.sys. Do not hide the real failure
            // behind a generic timeout: record the PnP state so the log clearly
            // shows whether the Apple CDC-NCM interfaces appeared and whether
            // Windows has a network driver bound to them.
            await WaitUntil(() => FindPhoneAdapter()?.OperationalStatus == OperationalStatus.Up, 35, "USB Ethernet adapter");
'@
$newBlock = @'
            await BindUsbNcmDriverAsync();
            await WaitUntil(() => FindPhoneAdapter()?.OperationalStatus == OperationalStatus.Up, 45, "USB Ethernet adapter");
'@
if ($text.Contains($oldBlock)) { $text = $text.Replace($oldBlock, $newBlock) }
else { throw 'Expected NCM wait block not found.' }

$method = @'
    private async Task BindUsbNcmDriverAsync()
    {
        var controls = FindPnP("VID_05AC&PID_12AB", null)
            .Where(d => d.Id.Contains("&MI_02\\", StringComparison.OrdinalIgnoreCase) || d.Id.Contains("&MI_04\\", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var d in controls) WriteLog($"NCM control interface: {d.Id} | {d.Name}");
        if (controls.Count == 0) { WriteLog("No iPad NCM control interface (MI_02/MI_04) visible."); return; }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[] {
            Path.Combine(windows, "INF", "usbncm.inf"),
            Path.Combine(windows, "INF", "netncm.inf")
        }.Where(File.Exists).ToList();
        if (candidates.Count == 0) {
            try {
                candidates = Directory.EnumerateFiles(Path.Combine(windows, "System32", "DriverStore", "FileRepository"), "usbncm.inf", SearchOption.AllDirectories).ToList();
            } catch { }
        }
        var inf = candidates.FirstOrDefault();
        WriteLog($"Windows NCM INF: {inf ?? "not found"}");
        if (inf is null) return;

        foreach (var hardwareId in new[] { "USB\\Class_02&SubClass_0d&Prot_00", "USB\\MS_COMP_WINNCM" })
        {
            var ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, inf, 0x5, out var reboot);
            var err = ok ? 0u : (uint)Marshal.GetLastWin32Error();
            WriteLog($"UsbNcm bind {hardwareId}: {(ok ? "success" : "failed")}, Win32Error={err}, rebootRequired={reboot}");
            if (ok) break;
            await Task.Delay(1000);
        }
    }

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

'@
if (-not $text.Contains('private async Task BindUsbNcmDriverAsync()')) {
    $needle = '    private void ConfigureUsbDevice(PnpDevice phone)'
    if (-not $text.Contains($needle)) { throw 'ConfigureUsbDevice anchor not found.' }
    $text = $text.Replace($needle, $method + $nl + $needle)
}

Set-Content -LiteralPath $path -Value $text -Encoding utf8 -NoNewline
Write-Host 'BuildFix completed: Windows UsbNcm binding enabled on iPad NCM control interfaces.'