using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace iPhoneUsbShare;

public partial class MainWindow : Window
{
    private readonly ShareEngine _engine;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _sharing;
    private bool _autoStarting;
    private bool _autoStartArmed = true;
    private bool _syncingMode;
    private HwndSource? _hwndSource;

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    /// <param name="modeOverride">Explicit --mode= from the command line; otherwise the last saved selection (Direct USB by default).</param>
    public MainWindow(ShareMode? modeOverride = null)
    {
        _engine = new ShareEngine(modeOverride ?? ShareModes.LoadSaved());
        InitializeComponent();
        SyncModeRadios(_engine.Mode);
        _engine.Log += (_, e) =>
        {
            Dispatcher.Invoke(() =>
                LogText.Text = $"[{DateTime.Now:HH:mm:ss}] {e}\n" + LogText.Text);
        };

        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            try
            {
                await StartupRecovery.RecoverWinUsbControlAsync(Log);
                await _engine.EnsurePrerequisitesAsync();
            }
            catch (Exception ex) { Log($"Setup check: {ex.Message}"); }
            await RefreshAsync();
            await TryAutoStartAsync("startup");
        };
        SourceInitialized += (_, _) =>
        {
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _hwndSource?.AddHook(WndProc);
        };
        Closed += (_, _) =>
        {
            _hwndSource?.RemoveHook(WndProc);
            _timer.Stop();
            _engine.WriteLog("iPhoneUsbShare session ended");
        };
    }

    private void SyncModeRadios(ShareMode mode)
    {
        _syncingMode = true;
        try
        {
            DirectModeRadio.IsChecked = mode == ShareMode.DirectUsb;
            ReverseModeRadio.IsChecked = mode == ShareMode.ReverseTethering;
        }
        finally { _syncingMode = false; }
    }

    // The mode can be switched at any time, including while a device is connected and
    // sharing: the engine restarts the network side (DHCP server / ICS) in the new mode
    // without redoing the USB bring-up. The selector is only locked for the switch itself.
    private async void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingMode) return;
        var selected = ReverseModeRadio.IsChecked == true ? ShareMode.ReverseTethering : ShareMode.DirectUsb;
        if (selected == _engine.Mode) return;

        ShareModes.Save(selected);
        ModePanel.IsEnabled = false;
        var wasSharing = _sharing;
        try
        {
            if (wasSharing) { _timer.Stop(); Log($"Switching to {selected.DisplayName()}…"); }
            await _engine.SwitchModeAsync(selected);
            if (wasSharing) { _timer.Start(); await RefreshAsync(); }
        }
        catch (Exception ex)
        {
            Log($"ERROR: mode switch: {ex.Message}");
            if (wasSharing)
            {
                // The new mode is selected but its session did not start; let the user retry with Start.
                _sharing = false;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
            await RefreshAsync();
        }
        finally { ModePanel.IsEnabled = true; }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var s = await _engine.GetStatusAsync();
            PhoneText.Text = s.AppleConnected ? s.AppleName : "Connect your iPhone or iPad by USB";
            AdapterText.Text = s.AdapterName is null ? "USB Ethernet: not connected" : $"USB Ethernet: {s.AdapterName} ({s.AdapterStatus})";
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                s.AppleConnected ? (s.AdapterName is not null ? "#16A34A" : "#D97706") : "#98A2B3"));
            StateText.Text = s.AdapterName is not null ? "USB network path is ON" : "Ready";
            IpText.Text = s.Lease ?? "—";
            RxText.Text = $"{s.Rx:0.0} KB/s";
            TxText.Text = $"{s.Tx:0.0} KB/s";
            if (_sharing && s.AdapterName is null)
            {
                _sharing = false;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }
        catch (Exception ex) { Log($"Status: {ex.Message}"); }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _autoStartArmed = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        try
        {
            Log("Starting USB network path…");

            // ShareEngine owns the complete reference-compatible transition:
            // usbccgp EnumeratorClass preparation -> registry index 2 -> restart
            // -> GET_MODE -> registry index 4 -> SET_MODE(3) -> NCM driver bind.
            // Do not use NcmConfigurationRecovery here; its old direct-config-5
            // path bypassed Apple's required mode-switch handshake and could leave
            // configuration 5 present without a Windows USB Ethernet adapter.
            await _engine.StartAsync();

            _sharing = true;
            StopButton.IsEnabled = true;
            Log("USB network path is ON.");
            _timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            StartButton.IsEnabled = true;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        try
        {
            _sharing = false;
            _autoStartArmed = false;
            _timer.Stop();
            Log("Stopping USB network path…");
            await _engine.StopAsync();
            StartButton.IsEnabled = true;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            StopButton.IsEnabled = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            if (wParam.ToInt32() == DBT_DEVICEARRIVAL)
                Dispatcher.BeginInvoke(new Action(() => _ = HandleDeviceArrivalAsync()));
            else if (wParam.ToInt32() == DBT_DEVICEREMOVECOMPLETE)
                Dispatcher.BeginInvoke(new Action(() => _ = HandleDeviceRemovalAsync()));
        }
        return IntPtr.Zero;
    }

    private async Task HandleDeviceArrivalAsync()
    {
        if (!_autoStartArmed || _autoStarting) return;
        await Task.Delay(750);
        await TryAutoStartAsync("USB device arrival");
    }

    private async Task HandleDeviceRemovalAsync()
    {
        await Task.Delay(250);
        try
        {
            if (_sharing)
            {
                Log("Apple USB device detected; reconciling additional USB sessions…");
                await _engine.StartAsync();
                await RefreshAsync();
                return;
            }

            var status = await _engine.GetStatusAsync();
            if (status.AppleConnected) return;
            _autoStartArmed = true;
            if (!_sharing && !_autoStarting) return;
            _sharing = false;
            _timer.Stop();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            Log("Apple USB device removed; USB network path is OFF.");
        }
        catch (Exception ex) { Log($"Device removal status: {ex.Message}"); }
    }

    private async Task TryAutoStartAsync(string reason)
    {
        if (!_autoStartArmed || _autoStarting) return;
        _autoStarting = true;
        try
        {
            var status = await _engine.GetStatusAsync();
            if (!status.AppleConnected) return;
            if (status.Sharing && status.AdapterName is not null && status.AdapterStatus == OperationalStatus.Up.ToString())
            {
                _sharing = true;
                StopButton.IsEnabled = true;
                _timer.Start();
                Log($"USB Ethernet already available; automatic startup satisfied by {reason}.");
                return;
            }

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            Log($"Apple USB device detected ({reason}); starting USB network path automatically…");
            await _engine.StartAsync();
            _sharing = true;
            StopButton.IsEnabled = true;
            _timer.Start();
            Log("Automatic USB network startup complete.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log($"Automatic USB startup failed: {ex.Message}");
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
        finally { _autoStarting = false; }
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = await _engine.DiagnosticsAsync();
            MessageBox.Show(this, text, "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _engine.WriteLog($"ERROR: Diagnostics: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Diagnostics failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Log(string message) => _engine.WriteLog(message);
}
