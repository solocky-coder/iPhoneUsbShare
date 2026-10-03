namespace iPhoneUsbShare;

/// <summary>
/// Collapses repetitive diagnostic lines in ActivityLog.txt. The WMI/ConfigMgr discovery code runs inside
/// polling loops and used to emit the same handful of lines hundreds of times per attempt (most of a
/// 1.4 MB log). Only the noisy prefixes below are throttled; every other message is written unchanged.
/// An identical throttled message is written at most once per <see cref="Window"/>, and the next line
/// that is let through reports how many copies were skipped.
/// </summary>
internal static class LogThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private static readonly string[] NoisyPrefixes =
    {
        "Apple USB child discovery:",
        "Apple USB network child discovery:",
        "Apple NCM adapter mapping: no adapter child matched",
        "WINUSB BINDING CHECK:",
        "WINUSB BINDING:",
        "WINUSB OPEN:",
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime LastWritten, int Suppressed)> State = new();

    /// <summary>Returns true when the message should be written. May append a "suppressed N" note to it.</summary>
    public static bool ShouldWrite(ref string message)
    {
        if (!IsNoisy(message)) return true;

        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (State.TryGetValue(message, out var entry) && now - entry.LastWritten < Window)
            {
                State[message] = (entry.LastWritten, entry.Suppressed + 1);
                return false;
            }

            var suppressed = State.TryGetValue(message, out var previous) ? previous.Suppressed : 0;
            State[message] = (now, 0);
            if (State.Count > 2000) State.Clear(); // bounded memory; worst case a few repeats are logged again
            if (suppressed > 0) message += $"  [+{suppressed} identical line(s) suppressed]";
            return true;
        }
    }

    private static bool IsNoisy(string message)
    {
        foreach (var prefix in NoisyPrefixes)
            if (message.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }
}
