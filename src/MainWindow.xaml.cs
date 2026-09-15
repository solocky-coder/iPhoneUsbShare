using System.Net.NetworkInformation;
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
        };
        Closed += (_, _) => { _timer.Stop(); _engine.WriteLog("iPhoneUsbShare session ended"); };
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
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        try
        {
            Log("Starting USB network path…");
            await _engine.EnsurePrerequisitesAsync();

            var recovered = await Task.Run(() => NcmConfigurationRecovery.ArmDirectNcm(Log));
            if (!recovered)
                throw new InvalidOperationException("Windows could not rebuild the Apple USB composite tree with CDC-NCM configuration 5.");

            // At this point usbccgp has rebuilt the composite device using
            // OriginalConfigurationValue=5. Do not call ShareEngine.StartAsync:
            // that legacy path deliberately resets to configuration 2 first,
            // which is exactly the sequencing that caused the missing-MI_02/03
            // failure. The USB transport is the product; ICS/DHCP is not part of
            // this bring-up step.
            await WaitForUsbEthernetAsync();
            _sharing = true;
            StopButton.IsEnabled = true;
            Log("USB Ethernet path is ON.");
            _timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            StartButton.IsEnabled = true;
        }
    }

    private async Task WaitForUsbEthernetAsync()
    {
        for (var i = 0; i < 60; i++)
        {
            var adapter = FindUsbEthernetAdapter();
            if (adapter is not null && adapter.OperationalStatus == OperationalStatus.Up)
            {
                Log($"USB Ethernet adapter is up: {adapter.Name}");
                return;
            }
            await Task.Delay(500);
        }

        var current = FindUsbEthernetAdapter();
        throw new InvalidOperationException(
            current is null
                ? "Apple CDC-NCM configuration 5 is present, but Windows did not expose a USB Ethernet adapter."
                : $"USB Ethernet adapter '{current.Name}' exists but is not operational ({current.OperationalStatus}).");
    }

    private static NetworkInterface? FindUsbEthernetAdapter()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .Where(n => n.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                        n.Description.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
                        n.Description.Contains("NCM", StringComparison.OrdinalIgnoreCase) ||
                        n.Description.Contains("UsbNcm", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .FirstOrDefault();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        try
        {
            _sharing = false;
            StartButton.IsEnabled = true;
            Log("USB network path stopped.");
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
