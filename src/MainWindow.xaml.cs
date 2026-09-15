using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace iPhoneUsbShare;

public partial class MainWindow : Window
{
    private readonly ShareEngine _engine = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _transportReady;

    public MainWindow()
    {
        InitializeComponent();
        _engine.Log += (_, e) => Dispatcher.Invoke(() =>
            LogText.Text = $"[{DateTime.Now:HH:mm:ss}] {e}\n" + LogText.Text);

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
        };
        Closed += (_, _) => { _timer.Stop(); _engine.WriteLog("iPhoneUsbShare session ended"); };
    }

    private async Task RefreshAsync()
    {
        try
        {
            var s = await _engine.GetStatusAsync();
            PhoneText.Text = s.AppleConnected ? s.AppleName : "Connect your iPhone or iPad by USB";
            AdapterText.Text = s.AdapterName is null ? "USB NCM Ethernet: not connected" : $"USB NCM Ethernet: {s.AdapterName} ({s.AdapterStatus})";
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                s.AppleConnected ? (s.AdapterStatus == "Up" ? "#16A34A" : "#D97706") : "#98A2B3"));
            StateText.Text = s.AdapterName is not null && s.AdapterStatus == "Up" ? "USB transport is ready" : "USB transport not ready";
            IpText.Text = s.Lease ?? "—";
            RxText.Text = $"{s.Rx:0.0} KB/s";
            TxText.Text = $"{s.Tx:0.0} KB/s";
            if (_transportReady && (s.AdapterName is null || s.AdapterStatus != "Up"))
            {
                _transportReady = false;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }
        catch (Exception ex) { Log($"Status: {ex.Message}"); }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        try
        {
            Log("Starting USB transport…");
            await _engine.EnsureUsbTransportAsync();
            _transportReady = true;
            StopButton.IsEnabled = true;
            Log("USB transport is ready. No ICS, NAT, DHCP or Wi-Fi configuration was performed.");
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
            await _engine.StopAsync();
            _transportReady = false;
            StartButton.IsEnabled = true;
            Log("USB transport stopped.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            StopButton.IsEnabled = true;
        }
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
