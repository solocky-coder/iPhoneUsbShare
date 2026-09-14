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
        // SET_MODE(3) can be accepted before usbccgp has finished
        // re-enumerating configuration 5. Do not give up after one PnP scan.
        // MI_02 is the valid CDC-NCM control interface: its descriptor has
        // exactly one interrupt endpoint. MI_04 has zero endpoints.
        List<PnpDevice> controls = new();
        var restartAttempted = false;

        for (var attempt = 1; attempt <= 30; attempt++)
        {
            controls = FindPnP("VID_05AC&PID_12AB", null)
                .Where(d => d.Id.Contains("&MI_02\\", StringComparison.OrdinalIgnoreCase) || d.Id.Contains("&MI_04\\", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (controls.Count > 0)
            {
                foreach (var d in controls) WriteLog($"NCM control interface: {d.Id} | {d.Name}");
                break;
            }

            if (attempt == 1 || attempt % 5 == 0)
            {
                WriteLog($"Waiting for iPad NCM control interfaces (attempt {attempt}/30)…");
                WriteLog($"Current USB identity: {UsbNative.GetDeviceId() ?? "unreachable"}; mode: {await UsbNative.GetModeAsync() ?? "unreachable"}");
                var apple = FindAppleDevice();
                WriteLog($"Current Apple PnP device: {apple?.Id ?? "not found"}");
            }

            if (!restartAttempted && attempt == 6)
            {
                restartAttempted = true;
                var apple = FindAppleDevice();
                if (apple is not null)
                {
                    WriteLog($"NCM interfaces still absent; restarting Apple device once: {apple.Id}");
                    try
                    {
                        RestartDevice(apple.Id);
                        WriteLog("Apple device restart requested; continuing NCM interface polling.");
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"Apple device restart for NCM re-enumeration failed: {ex.Message}");
                    }
                }
            }

            await Task.Delay(500);
        }

        if (controls.Count == 0)
        {
            WriteLog("No iPad NCM control interface (MI_02/MI_04) visible after re-enumeration polling.");
            WriteLog($"Final USB identity: {UsbNative.GetDeviceId() ?? "unreachable"}; mode: {await UsbNative.GetModeAsync() ?? "unreachable"}");
            throw new InvalidOperationException("Apple accepted CDC-NCM mode, but Windows did not expose MI_02/MI_04. The device did not finish CDC-NCM re-enumeration.");
        }

        var target = controls.FirstOrDefault(d => d.Id.Contains("&MI_02\\", StringComparison.OrdinalIgnoreCase))
                     ?? controls.FirstOrDefault(d => d.Id.Contains("&MI_04\\", StringComparison.OrdinalIgnoreCase));
        if (target is null) return;
        WriteLog($"Selected NCM control interface for UsbNcm: {target.Id} | {target.Name}");

        // UpdateDriverForPlugAndPlayDevicesW takes a HARDWARE ID, not a
        // device-instance ID. Use the MI-specific hardware ID for matching.
        var targetHardwareId = target.Id.Contains("&MI_02\\", StringComparison.OrdinalIgnoreCase)
            ? "USB\\VID_05AC&PID_12AB&MI_02"
            : "USB\\VID_05AC&PID_12AB&MI_04";
        WriteLog($"NCM target hardware ID: {targetHardwareId}");

        // Capture Windows' own PnP view while the devnode is still present.
        // This is deliberately done before any driver update/restart so that
        // the log records the hardware IDs, compatible IDs, current driver,
        // matching drivers, rank, and problem state that Windows sees.
        var beforePnp = RunAllowRestart("pnputil.exe", $"/enum-devices /instanceid \"{target.Id}\" /ids /drivers");
        WriteLog($"NCM PnP diagnostic before bind exit code: {beforePnp.ExitCode}");
        if (!string.IsNullOrWhiteSpace(beforePnp.Output))
            WriteLog("NCM PnP diagnostic before bind output: " + beforePnp.Output.Trim());
        if (!string.IsNullOrWhiteSpace(beforePnp.Error))
            WriteLog("NCM PnP diagnostic before bind error: " + beforePnp.Error.Trim());

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

        var add = RunAllowRestart("pnputil.exe", $"/add-driver \"{inf}\" /install");
        WriteLog($"UsbNcm package registration exit code: {add.ExitCode}");
        if (!string.IsNullOrWhiteSpace(add.Output)) WriteLog($"UsbNcm package output: {add.Output.Trim()}");
        if (!string.IsNullOrWhiteSpace(add.Error)) WriteLog($"UsbNcm package error: {add.Error.Trim()}");

        // Capture the ranking again after usbncm.inf has been registered.
        var rankedPnp = RunAllowRestart("pnputil.exe", $"/enum-devices /instanceid \"{target.Id}\" /ids /drivers");
        WriteLog($"NCM PnP diagnostic after INF registration exit code: {rankedPnp.ExitCode}");
        if (!string.IsNullOrWhiteSpace(rankedPnp.Output))
            WriteLog("NCM PnP diagnostic after INF registration output: " + rankedPnp.Output.Trim());
        if (!string.IsNullOrWhiteSpace(rankedPnp.Error))
            WriteLog("NCM PnP diagnostic after INF registration error: " + rankedPnp.Error.Trim());

        var ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, targetHardwareId, inf, 0x5, out var reboot);
        var err = ok ? 0u : (uint)Marshal.GetLastWin32Error();
        WriteLog($"UsbNcm exact bind {targetHardwareId} -> {target.Id}: {(ok ? "success" : "failed")}, Win32Error={err}, rebootRequired={reboot}");
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

        // Capture the state before restarting the devnode. If binding failed,
        // this is the last moment where Windows may still expose the original
        // driver/ranking information for the selected interface.
        var postBindPnp = RunAllowRestart("pnputil.exe", $"/enum-devices /instanceid \"{target.Id}\" /ids /drivers");
        WriteLog($"NCM PnP diagnostic after bind attempt exit code: {postBindPnp.ExitCode}");
        if (!string.IsNullOrWhiteSpace(postBindPnp.Output))
            WriteLog("NCM PnP diagnostic after bind attempt output: " + postBindPnp.Output.Trim());
        if (!string.IsNullOrWhiteSpace(postBindPnp.Error))
            WriteLog("NCM PnP diagnostic after bind attempt error: " + postBindPnp.Error.Trim());

        await Task.Delay(1500);
        try
        {
            RestartDevice(target.Id);
            WriteLog($"NCM control device restart requested: {target.Id}");
        }
        catch (Exception ex)
        {
            WriteLog($"NCM control device restart failed: {ex.Message}");
        }

        await Task.Delay(1500);
        var state = FindPnP(target.Id, null).FirstOrDefault();
        WriteLog($"Selected NCM control state after bind: {state?.Name ?? "not found"}");
    }

'@
$text = $text.Substring(0, $start) + $replacement + $text.Substring($end)
Set-Content -LiteralPath $path -Value $text -Encoding UTF8
