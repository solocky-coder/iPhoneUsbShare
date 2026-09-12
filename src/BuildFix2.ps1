$ErrorActionPreference = 'Stop'
$p = Join-Path $PSScriptRoot 'ShareEngine.cs'
$t = Get-Content -LiteralPath $p -Raw
$a = '        var sysDst = Path.Combine(Environment.SystemDirectory, "drivers", "libusb0.sys");'
$o = '        if (installResult.ExitCode != 0) throw new InvalidOperationException($"install-filter.exe failed ({installResult.ExitCode}): {installResult.Error}");'
if (-not $t.Contains($o)) { throw 'install anchor not found' }
$n = $o + @'
        if (phone.Id.Contains("&MI_01\\", StringComparison.OrdinalIgnoreCase))
        {
            var hid = $"USB\\VID_{Vendor}&PID_{GetApplePid(phone.Id)}&MI_01";
            var r2 = RunAllowRestart(installer, $"install --device=\"{hid}\"");
            WriteLog($"libusb MI_01 filter installer exit code: {r2.ExitCode}");
            if (!string.IsNullOrWhiteSpace(r2.Output)) WriteLog($"libusb MI_01 installer output: {r2.Output.Trim()}");
            if (!string.IsNullOrWhiteSpace(r2.Error)) WriteLog($"libusb MI_01 installer error: {r2.Error.Trim()}");
            if (r2.ExitCode != 0) throw new InvalidOperationException($"MI_01 filter install failed ({r2.ExitCode}): {r2.Error}");
            try { var rr = RunAllowRestart("pnputil.exe", $"/restart-device \"{phone.Id}\""); WriteLog($"Apple MI_01 restart exit code: {rr.ExitCode}"); } catch (Exception ex) { WriteLog($"Apple MI_01 restart: {ex.Message}"); }
        }
        await Task.Delay(2000);
'@
$t = $t.Replace($o,$n)
$w = '        await WaitUntil(() => UsbNative.IsReachable(), 15, "Apple USB filter driver");'
$r = @'
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (UsbNative.IsReachable()) return;
            WriteLog($"Apple USB filter retry {attempt}/3: libusb still cannot enumerate the device.");
            try { RestartDevice(phone.Id); } catch (Exception ex) { WriteLog($"Apple USB restart retry: {ex.Message}"); }
            await Task.Delay(2500);
        }
        throw new TimeoutException("Timed out waiting for Apple USB filter driver.");
'@
if (-not $t.Contains($w)) { throw 'wait anchor not found' }
$t = $t.Replace($w,$r)
Set-Content -LiteralPath $p -Value $t -Encoding utf8 -NoNewline
