$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -LiteralPath $path -Raw
$nl = [Environment]::NewLine

# The NCM binder uses the Win32 P/Invoke declarations below.
if (-not $text.Contains('using System.Runtime.InteropServices;')) {
    $text = $text.Replace('using System.Net.Http;' + $nl, 'using System.Net.Http;' + $nl + 'using System.Runtime.InteropServices;' + $nl)
}

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

        // MI_02 is currently claimed by the legacy Apple Mobile Device Ethernet
        // driver on the user's Windows 10 machine. Prefer the unclaimed NCM
        // control interface MI_04, which is paired with data interface MI_05.
        var target = controls.FirstOrDefault(d => d.Id.Contains("&MI_04\\", StringComparison.OrdinalIgnoreCase))
                     ?? controls.FirstOrDefault(d => d.Id.Contains("&MI_02\\", StringComparison.OrdinalIgnoreCase));
        if (target is null) return;
        WriteLog($"Selected NCM control interface for UsbNcm: {target.Id} | {target.Name}");

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

        // Make sure the inbox package is registered, then bind the exact control
        // interface. UpdateDriverForPlugAndPlayDevices can match the device's
        // compatible NCM class IDs from UsbNcm.inf.
        var add = RunAllowRestart("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"UsbNcm package registration exit code: {add.ExitCode}");
        if (!string.IsNullOrWhiteSpace(add.Output)) WriteLog($"UsbNcm package output: {add.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(add.Error)) WriteLog($"UsbNcm package error: {add.Error.Trim()}");

        var exactId = target.Id;
        var ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, exactId, inf, 0x5, out var reboot);
        var err = ok ? 0u : (uint)Marshal.GetLastWin32Error();
        WriteLog($"UsbNcm bind {exactId}: {(ok ? "success" : "failed")}, Win32Error={err}, rebootRequired={reboot}");
        if (!ok)
        {
            foreach (var hardwareId in new[] { "USB\\MS_COMP_WINNCM", "USB\\Class_02&SubClass_0d&Prot_00" })
            {
                ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, inf, 0x5, out reboot);
                err = ok ? 0u : (uint)Marshal.GetLastWin32Error();
                WriteLog($"UsbNcm fallback bind {hardwareId}: {(ok ? "success" : "failed")}, Win32Error={err}, rebootRequired={reboot}");
                if (ok) break;
                await Task.Delay(1000);
            }
        }
        await Task.Delay(1500);
        WriteLog($"Selected NCM control state after bind: {FindPnP(target.Id, null).FirstOrDefault()?.Name ?? "not found"}");
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
Write-Host 'BuildFix completed: iPad MI_04 is preferred and UsbNcm binding is enabled.'