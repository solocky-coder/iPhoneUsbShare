using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace iPhoneUsbShare;

public partial class MainWindow : Window
{
    private readonly ShareEngine _engine = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _sharing;

    public MainWindow()
    {
        InitializeComponent();
        _engine.Log += (_, e) => Dispatcher.Invoke(() =>
            LogText.Text = $"[{DateTime.Now:HH:mm:ss}] {e}\n" + LogText.Text);

        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            try { await _engine.EnsurePrerequisitesAsync(); }
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
            PhoneText.Text = s.PhoneConnected ? s.PhoneName : "Connect your iPhone or iPad by USB";
            AdapterText.Text = s.AdapterName is null ? "USB Ethernet: not connected" : $"USB Ethernet: {s.AdapterName} ({s.AdapterStatus})";
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                s.PhoneConnected ? (s.Sharing ? "#16A34A" : "#D97706") : "#98A2B3"));
            StateText.Text = s.Sharing ? "Internet sharing is ON" : "Ready";
            IpText.Text = s.PhoneIp ?? "—";
            RxText.Text = $"{s.RxKbps:0.0} KB/s";
            TxText.Text = $"{s.TxKbps:0.0} KB/s";
            if (_sharing && !s.Sharing && s.AdapterName is null)
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
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        try
        {
            Log("Starting…");
            await _engine.StartAsync();
            _sharing = true;
            StopButton.IsEnabled = true;
            Log("Sharing is ON.");
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
            _sharing = false;
            StartButton.IsEnabled = true;
            Log("Sharing stopped.");
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
