using System.Threading;

namespace iPhoneUsbShare;

/// <summary>
/// Headless equivalent of what MainWindow's Loaded and StartButton_Click
/// handlers do when a window is actually shown. Used only when launched
/// with --hidden --stop-event=&lt;name&gt; (see App.xaml.cs).
///
/// This adds no new IPC: ShareEngine.WriteLog already writes every line to
/// ActivityLog.txt regardless of who's listening to the Log event, and that
/// file is DYSEKT's side of the channel (AppleUsbShareLauncher tails it by
/// byte offset). Running headless just means nothing here needs to touch a
/// Dispatcher or update any UI.
/// </summary>
internal sealed class HiddenHostService
{
    private readonly string _stopEventName;
    private readonly ShareEngine _engine;

    public HiddenHostService(string stopEventName, ShareMode mode = ShareMode.DirectUsb)
    {
        _stopEventName = stopEventName;
        _engine = new ShareEngine(mode);
    }

    public async Task RunAsync()
    {
        try
        {
            // Mirrors MainWindow's Loaded handler exactly (setup checks
            // before the engine is used), then StartButton_Click's call
            // into StartAsync() — see the "Do not use NcmConfigurationRecovery
            // here" comment in MainWindow.xaml.cs for why StartupRecovery
            // and StartAsync's own handshake stay separate.
            await StartupRecovery.RecoverWinUsbControlAsync(_engine.WriteLog);
            await _engine.EnsurePrerequisitesAsync();
            await _engine.StartAsync();
        }
        catch (Exception ex)
        {
            // There's no retry button in headless mode. Log it (DYSEKT's
            // launcher surfaces this line through its own status/log) and
            // fall through to waiting on the stop event anyway, so DYSEKT
            // can still shut this process down cleanly instead of it being
            // stuck running elevated with sharing half-started.
            _engine.WriteLog($"ERROR: {ex.Message}");
        }

        WaitForStopSignal();

        try
        {
            await _engine.StopAsync();
        }
        catch (Exception ex)
        {
            _engine.WriteLog($"Stop cleanup: {ex.Message}");
        }
    }

    private void WaitForStopSignal()
    {
        try
        {
            // DYSEKT (unelevated) creates this event before launching us
            // elevated, so it already exists by the time we get here —
            // OpenExisting, not a fresh create. An elevated process opening
            // a lower-integrity process's named object is permitted by
            // Windows' mandatory-integrity policy; only the reverse
            // direction (low opening high) is restricted, which is exactly
            // why DYSEKT has to be the one that creates it.
            using var stopEvent = EventWaitHandle.OpenExisting(_stopEventName);
            stopEvent.WaitOne();
            _engine.WriteLog("Stop signal received.");
        }
        catch (Exception ex)
        {
            // If DYSEKT's own process dies without signaling (killed, not
            // stopped), the event it created dies with it and OpenExisting
            // throws here. There's no clean signal to wait on in that case;
            // log it and stop anyway rather than leaving the USB
            // configuration wherever the mode-switch handshake left it.
            _engine.WriteLog($"Stop event unavailable ({ex.Message}); stopping anyway.");
        }
    }
}
