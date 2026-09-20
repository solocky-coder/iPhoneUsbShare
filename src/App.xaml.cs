using System.Windows;

namespace iPhoneUsbShare;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool hidden = false;
        string? stopEventName = null;
        ShareMode? mode = null;
        string? badMode = null;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--hidden", StringComparison.OrdinalIgnoreCase))
                hidden = true;
            else if (arg.StartsWith("--stop-event=", StringComparison.OrdinalIgnoreCase))
                stopEventName = arg["--stop-event=".Length..];
            else if (arg.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
            {
                var text = arg["--mode=".Length..];
                if (ShareModes.TryParse(text, out var parsed)) mode = parsed;
                else badMode = text;
            }
        }

        if (badMode is not null)
        {
            // An unknown mode must not silently fall back: reverse tethering
            // shares this PC's internet, so guessing is not acceptable.
            if (!hidden) MessageBox.Show($"Unknown --mode value '{badMode}'. Use --mode=direct or --mode=reverse.", "iPhoneUsbShare", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (!hidden)
        {
            // App.xaml no longer sets StartupUri, so this is now the only
            // place a normal (non-hidden) launch creates and shows
            // MainWindow. ShutdownMode stays at its default
            // (OnLastWindowClose), so behavior here is otherwise identical
            // to what StartupUri used to do.
            var window = new MainWindow(mode);
            MainWindow = window;
            window.Show();
            return;
        }

        if (string.IsNullOrEmpty(stopEventName))
        {
            // Hidden mode with no stop event means DYSEKT has no way to ask
            // us to shut down gracefully. Fail loudly instead of running
            // orphaned and elevated with nothing to stop it.
            Shutdown(1);
            return;
        }

        // No window ever opens in hidden mode, so nothing triggers the
        // usual OnLastWindowClose shutdown; HiddenHostService.RunAsync()
        // returning is what ends the process instead.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Hidden mode is driven by DYSEKT, which historically got the isolated Direct USB
        // link. Keep that as the default (never the GUI's saved selection) so launching
        // headless can't unexpectedly turn on internet sharing; pass --mode=reverse to opt in.
        var service = new HiddenHostService(stopEventName, mode ?? ShareMode.DirectUsb);
        await service.RunAsync();
        Shutdown();
    }
}
