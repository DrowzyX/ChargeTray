namespace ChargeTray;

// Listen to unsolicited Xenta battery frames without sending device commands.
// A persistent listener is needed because reports arrive roughly every 2s.
internal sealed class XentaReceiver : IDisposable {
    private readonly object _gate = new();
    private string[] _paths = [];
    private CancellationTokenSource? _stop;
    private Task? _listener;
    private long _generation;
    private int? _battery;
    private DateTime _updatedUtc;
    private string _details = "ожидание HID-отчёта";
    private bool _disposed;
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(45);

    internal Task<(bool Found, int? Level, string Details)> Read(bool force) {
        var paths = RazerReceiver.PresentInterfaces(p =>
            p.StartsWith(@"\\?\hid#vid_1d57&pid_fa60", StringComparison.OrdinalIgnoreCase));
        lock (_gate) {
            if (!_paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) {
                StopListener();
                _paths = paths;
                _battery = null;
                _updatedUtc = default;
                _details = "ожидание HID-отчёта";
            }
            if (_disposed || paths.Length == 0)
                return Task.FromResult((false, (int?)null, "ресивер не подключён"));

            if (_listener is null || _listener.IsCompleted) {
                _stop?.Dispose();
                _stop = new CancellationTokenSource();
                var generation = ++_generation;
                var token = _stop.Token;
                _listener = Task.Run(() => ListenAsync(paths, generation, token), token);
            }
            bool fresh = _battery.HasValue && DateTime.UtcNow - _updatedUtc <= FreshFor;
            var note = fresh ? $"{_battery}% · подтверждённый HID-отчёт" :
                _battery.HasValue ? "последний ответ устарел (>45 с); ждём новый HID-отчёт" : _details;
            return Task.FromResult((true, fresh ? _battery : null, note));
        }
    }

    private async Task ListenAsync(string[] paths, long generation, CancellationToken stop) {
        while (!stop.IsCancellationRequested) {
            string? candidate = null;
            foreach (var path in paths) {
                using var metadata = RazerReceiver.Open(path, 0);
                if (RazerReceiver.GetCaps(metadata) is { Page: 0x000A, Usage: 0x0000, Input: 5 }) {
                    candidate = path;
                    break;
                }
            }
            if (candidate is null) {
                SetDetail(generation, "HID-коллекция заряда (000A:0000, IN=5) не обнаружена");
                return;
            }
            try {
                using var handle = RazerReceiver.Open(candidate, 0x80000000, overlapped: true);
                if (handle.IsInvalid) {
                    SetDetail(generation, "нет доступа на чтение HID (000A:0000)");
                } else {
                    using var stream = new FileStream(handle, FileAccess.Read, 5, isAsync: true);
                    var confirm = new XentaFrameConfirmation();
                    var buffer = new byte[5];
                    SetDetail(generation, "слушаем входящие HID-отчёты (000A:0000)");
                    while (!stop.IsCancellationRequested) {
                        int count = await stream.ReadAsync(buffer.AsMemory(), stop).ConfigureAwait(false);
                        if (count == 0) break;
                        if (count != 5) continue;
                        var now = DateTime.UtcNow;
                        int? level = confirm.Observe(buffer, now);
                        if (level is not null) {
                            lock (_gate) {
                                if (_disposed || generation != _generation) return;
                                _battery = level;
                                _updatedUtc = now;
                                _details = $"{level}% · 2 подтверждённых HID-отчёта";
                            }
                        } else if (XentaBatteryPacket.Percent(buffer) is null) {
                            SetDetailIfEmpty(generation, "пакет заряда не найден; последний HID: " +
                                Convert.ToHexString(buffer));
                        }
                    }
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
              catch (IOException) { SetDetail(generation, "ошибка чтения HID; повторное подключение"); }
              catch (UnauthorizedAccessException) { SetDetail(generation, "доступ к HID заблокирован другим приложением"); }
              catch (Exception e) { SetDetail(generation, "HID: " + e.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(4), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetDetail(long generation, string value) {
        lock (_gate) if (!_disposed && generation == _generation) _details = value;
    }
    private void SetDetailIfEmpty(long generation, string value) {
        lock (_gate) if (!_disposed && generation == _generation && _battery is null) _details = value;
    }
    // Called under _gate.
    private void StopListener() {
        ++_generation;
        var old = _stop;
        var oldTask = _listener;
        old?.Cancel();
        if (old is not null) {
            if (oldTask is null || oldTask.IsCompleted) old.Dispose();
            else _ = oldTask.ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        }
        _stop = null;
        _listener = null;
    }
    public void Dispose() {
        lock (_gate) {
            if (_disposed) return;
            _disposed = true;
            StopListener();
        }
    }
}
