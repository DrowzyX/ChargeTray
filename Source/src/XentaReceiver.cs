using System.Runtime.InteropServices;
namespace ChargeTray;

// Read-only HID listener. Never issue a vendor command or modify the HID device.
internal sealed class XentaReceiver {
    private Task<(int? Level, string Note)>? _pending;
    private string[] _paths = [];
    private int? _last;
    private DateTime _lastSeen;
    internal bool RetrySoon { get; private set; }
    internal async Task<(bool Found, int? Level, string Details)> Read(bool force) {
        var paths = RazerReceiver.PresentInterfaces(p =>
            p.StartsWith(@"\\?\hid#vid_1d57&pid_fa60", StringComparison.OrdinalIgnoreCase));
        if (!_paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) {
            _paths = paths; _pending = null; _last = null;
        }
        if (paths.Length == 0) return (false, null, "не подключён");
        RetrySoon = _last is null;
        if (_pending is null && (force || DateTime.UtcNow - _lastSeen > TimeSpan.FromSeconds(15)))
            _pending = Task.Run(() => ReadOnce(paths));
        if (_pending is not null) try {
            // Never block the UI thread for a receiver that's asleep.
            var answer = await _pending.WaitAsync(TimeSpan.FromMilliseconds(650));
            _pending = null;
            if (answer.Level.HasValue) { _last = answer.Level; _lastSeen = DateTime.UtcNow; }
            return (true, answer.Level ?? (DateTime.UtcNow - _lastSeen < TimeSpan.FromSeconds(90) ? _last : null), answer.Note);
        } catch (TimeoutException) { return (true, DateTime.UtcNow - _lastSeen < TimeSpan.FromSeconds(90) ? _last : null, "ожидание HID-отчёта"); }
        catch (Exception e) { _pending = null; return (true, null, e.GetType().Name); }
        return (true, _last, "ожидание ответа");
    }
    private static (int?, string) ReadOnce(IEnumerable<string> paths) {
        foreach (var path in paths) {
            using var meta = RazerReceiver.Open(path, 0);
            var caps = RazerReceiver.GetCaps(meta);
            if (caps is not { Page: 0x000A, Input: 5 }) continue;
            // Open with read-only access and no output/feature control channel.
            using var handle = RazerReceiver.Open(path, 0x80000000, overlapped: true);
            if (handle.IsInvalid) continue;
            try {
                using var stream = new FileStream(handle, FileAccess.Read, 5, isAsync: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1700));
                byte[] bytes = new byte[5];
                int confirmed = 0, last = -1;
                while (!timeout.IsCancellationRequested) {
                    int received = stream.ReadAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
                    if (received != 5) continue;
                    if (XentaBatteryPacket.Percent(bytes) is not int level) continue;
                    if (level == last) confirmed++; else { last = level; confirmed = 1; }
                    if (confirmed >= 2) return (level, $"{level}% · 2 подтверждённых HID-отчёта");
                }
            } catch (OperationCanceledException) { }
              catch (IOException) { }
              catch (UnauthorizedAccessException) { }
        }
        return (null, "подходящий отчёт заряда пока не получен");
    }
}
