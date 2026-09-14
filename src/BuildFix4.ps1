$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -Raw -LiteralPath $path
$start = $text.IndexOf('    private async Task BindUsbNcmDriverAsync()')
$endMarker = '    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]'
$end = $text.IndexOf($endMarker, $start)
if ($start -lt 0 -or $end -lt 0) { throw 'BuildFix4: BindUsbNcmDriverAsync anchor not found.' }
$replacement = @'
    private async Task BindUsbNcmDriverAsync()
    {
        var controls = FindPnP("VID_05AC&PID_12AB", null)
            .Where(d => d.Id.Contains("&MI_02\", StringComparison.OrdinalIgnoreCase) || d.Id.Contains("&MI_04\", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var d in controls) WriteLog($"NCM control interface: {d.Id} | {d.Name}");
        if (controls.Count == 0)
        {
            WriteLog("No iPad NCM control interface (MI_02/MI_04) visible.");
            return;
        }

        // Prefer MI_02: captured CDC-NCM descriptors show MI_02 has one
        // interrupt endpoint, while MI_04 has zero endpoints.
        var target = controls.FirstOrDefault(d => d.Id.Contains("&MI_02\", StringComparison.OrdinalIgnoreCase))
                     ?? controls.FirstOrDefault(d => d.Id.Contains("&MI_04\", StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        var hardwareId = target.Id;
        var lastSlash = hardwareId.LastIndexOf('\\');
        if (lastSlash > 0) hardwareId = hardwareId[..lastSlash];
        WriteLog($"Selected NCM control interface for UsbNcm: {target.Id} | {target.Name}");
        WriteLog($"NCM hardware ID for driver matching: {hardwareId}");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(windows, "INF", "usbncm.inf"),
            Path.Combine(windows, "INF", "netncm.inf")
        }.Where(File.Exists).ToList();
        if (candidates.Count == 0)
        {
            try
            {
                candidates = Directory.EnumerateFiles(
                    Path.Combine(windows, "System32", "DriverStore", "FileRepository"),
                    "usbncm.inf", SearchOption.AllDirectories).ToList();
            }
            catch { }
        }
        var inf = candidates.FirstOrDefault();
        WriteLog($"Windows NCM INF: {inf ?? "not found"}");
        if (inf is null) return;

        var before = RunAllowRestart("pnputil.exe", $"/enum-devices /instanceid \"{target.Id}\" /drivers");
        WriteLog($"NCM driver ranking query exit code: {before.ExitCode}");
        if (!string.IsNullOrWhiteSpace(before.Output)) WriteLog($"NCM driver ranking: {before.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(before.Error)) WriteLog($"NCM driver ranking error: {before.Error.Trim()}");

        var add = RunAllowRestart("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"UsbNcm package registration exit code: {add.ExitCode}");
        if (!string.IsNullOrWhiteSpace(add.Output)) WriteLog($"UsbNcm package output: {add.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(add.Error)) WriteLog($"UsbNcm package error: {add.Error.Trim()}");

        var ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, inf, 0x5, out var reboot);
        var err = ok ? 0u : (uint)Marshal.GetLastWin32Error();
        WriteLog($"UsbNcm hardware-ID bind {hardwareId}: {(ok ? "success" : "failed")}, Win32Error={err}, rebootRequired={reboot}");
        if (!ok)
        {
            foreach (var id in new[] { "USB\\MS_COMP_WINNCM", "USB\\Class_02&SubClass_0d&Prot_00" })
            {
                ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, id, inf, 0x5, out reboot);
                err = ok ? 0u : (uint)Marshal.GetLastWin32Error();
                WriteLog($"UsbNcm fallback bind {id}: {(ok ? "success" : "failed")}, Win32Error={err}, rebootRequired={reboot}");
                if (ok) break;
            }
        }

        var restart = RunAllowRestart("pnputil.exe", $"/restart-device \"{target.Id}\"");
        WriteLog($"NCM control restart exit code: {restart.ExitCode}");
        if (!string.IsNullOrWhiteSpace(restart.Output)) WriteLog($"NCM control restart output: {restart.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(restart.Error)) WriteLog($"NCM control restart error: {restart.Error.Trim()}");
        await Task.Delay(2500);

        var after = RunAllowRestart("pnputil.exe", $"/enum-devices /instanceid \"{target.Id}\" /drivers");
        WriteLog($"NCM driver ranking after bind exit code: {after.ExitCode}");
        if (!string.IsNullOrWhiteSpace(after.Output)) WriteLog($"NCM driver ranking after bind: {after.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(after.Error)) WriteLog($"NCM driver ranking after bind error: {after.Error.Trim()}");

        var state = FindPnP(target.Id, null).FirstOrDefault();
        WriteLog($"Selected NCM control state after bind: {state?.Name ?? "not found"}");
    }

'@
$text = $text.Substring(0, $start) + $replacement + $text.Substring($end)
Set-Content -LiteralPath $path -Value $text -Encoding UTF8
