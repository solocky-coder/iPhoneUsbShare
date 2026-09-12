$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -LiteralPath $path -Raw
$nl = [Environment]::NewLine

# Remove the legacy MI_01 libusb child filter injected by BuildFix2.
$mi01Start = $text.IndexOf('        if (phone.Id.Contains("&MI_01\\", StringComparison.OrdinalIgnoreCase))')
if ($mi01Start -ge 0) {
    $mi01End = $text.IndexOf('        await Task.Delay(2000);', $mi01Start)
    if ($mi01End -lt 0) { throw 'Could not locate end of legacy MI_01 filter block.' }
    $mi01End += ('        await Task.Delay(2000);').Length
    $text = $text.Remove($mi01Start, $mi01End - $mi01Start)
}

$oldWait = @'
            // Windows 10 has no inbox UsbNcm.sys. Do not hide the real failure
            // behind a generic timeout: record the PnP state so the log clearly
            // shows whether the Apple CDC-NCM interfaces appeared and whether
            // Windows has a network driver bound to them.
            await WaitUntil(() => FindPhoneAdapter()?.OperationalStatus == OperationalStatus.Up, 35, "USB Ethernet adapter");
'@
$newWait = @'
            PrepareUsbNcmManualBinding();
            await WaitUntil(() => FindPhoneAdapter()?.OperationalStatus == OperationalStatus.Up, 60, "USB Ethernet adapter");
'@
if ($text.Contains($oldWait)) {
    $text = $text.Replace($oldWait, $newWait)
} elseif (-not $text.Contains('PrepareUsbNcmManualBinding();')) {
    throw 'Expected Windows 10 UsbNcm wait block was not found.'
}

if (-not $text.Contains('private void PrepareUsbNcmManualBinding()')) {
$method = @'
    private void PrepareUsbNcmManualBinding()
    {
        try
        {
            var controlInterfaces = FindPnP("VID_05AC&PID_12AB", null)
                .Where(d => d.Id.Contains("&MI_02\\", StringComparison.OrdinalIgnoreCase) ||
                            d.Id.Contains("&MI_04\\", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var d in controlInterfaces)
                WriteLog($"NCM control interface candidate: {d.Id} | {d.Name}");

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var store = Path.Combine(windows, "System32", "DriverStore", "FileRepository");
            var inf = Directory.Exists(store)
                ? Directory.EnumerateFiles(store, "usbncm.inf", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            var direct = Path.Combine(windows, "INF", "usbncm.inf");
            inf ??= File.Exists(direct) ? direct : null;

            WriteLog($"Microsoft UsbNcm.inf: {inf ?? "not found"}");
            if (inf is not null)
            {
                WriteLog("Windows 10 NCM driver is present. If no adapter appears, bind this INF manually to the iPad CDC-NCM control interface (MI_02 or MI_04) as Microsoft -> UsbNcm Host Device.");
                Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            WriteLog($"NCM driver preparation: {ex.Message}");
        }
    }

'@
    $anchor = '    private void ConfigureUsbDevice(PnpDevice phone)'
    if (-not $text.Contains($anchor)) { throw 'ConfigureUsbDevice anchor not found.' }
    $text = $text.Replace($anchor, $method + $nl + $anchor)
}

Set-Content -LiteralPath $path -Value $text -Encoding utf8 -NoNewline
Write-Host 'BuildFix3 completed: removed legacy MI_01 filter and added NCM driver diagnostics.'
