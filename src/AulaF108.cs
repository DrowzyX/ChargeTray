namespace ChargeTray;

internal sealed class AulaF108 {
    internal const string Name = "AULA F108 Pro";
    internal const string Id = "05AC:024F";
    private const string Prefix = @"\\?\hid#vid_05ac&pid_024f";
    private string[] _paths = [];
    private string[] _pendingPaths = [];
    private (bool Receiver, int? Percent, string Details) _last;
    private DateTime _next;
    private AulaReadingCache _cache = new();
    internal bool RetrySoon { get; private set; }
    private Task<(bool Receiver, int? Percent, string Details)>? _pending;

    internal static bool IsKnownInput(HidInput.Input input) => input.Hardware.Equals(Id, StringComparison.OrdinalIgnoreCase) &&
        KnownProduct(input.Name);

    private static bool KnownProduct(string? value) {
        var name = value?.Replace(" ", "", StringComparison.Ordinal).Trim();
        return string.Equals(name, "F108ProDongle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "AULAF108Pro", StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<(bool Receiver, int? Percent, string Details, bool Stale)> Read(bool force, bool knownFromRawInput) {
        var paths = RazerReceiver.PresentInterfaces(p => p.Length > Prefix.Length &&
            p.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && p[Prefix.Length] is '&' or '#');
        if (!_paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) {
            _paths = paths;
            _next = DateTime.MinValue;
            _last = default;
            _cache = new AulaReadingCache();
        }
        RetrySoon = false;
        if (paths.Length == 0) return (knownFromRawInput, null, "не найдены интерфейсы HID 05AC:024F", false);

        (bool, int?, string, bool) Visible((bool Receiver, int? Percent, string Details) reply) {
            RetrySoon = reply.Receiver && !reply.Percent.HasValue && _cache.Failures <= 3;
            var shown = _cache.Display(reply.Percent, DateTime.UtcNow);
            return (reply.Receiver, shown.Percent, reply.Details + (shown.Stale ? $" · последнее: {shown.Percent}%" : ""), shown.Stale);
        }
        if (_pending is null && (force || DateTime.UtcNow >= _next)) {
            _pendingPaths = paths;
            _pending = Task.Run(() => QueryReceiver(paths, knownFromRawInput));
        }
        if (_pending is not null) {
            try {
                var result = await _pending.WaitAsync(TimeSpan.FromSeconds(2));
                _pending = null;
                if (!_pendingPaths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
                    return Visible((knownFromRawInput, null, "ресивер изменился, ожидается повторный опрос"));
                _last = result;
                _cache.Record(result.Percent, DateTime.UtcNow);
                _next = DateTime.UtcNow.AddSeconds(result.Percent.HasValue ? 50 : _cache.Failures <= 3 ? 6 : 16);
            } catch (TimeoutException) {
                _cache.Record(null, DateTime.UtcNow);
                return Visible((knownFromRawInput || _last.Receiver, null, "ожидание ответа драйвера"));
            } catch (Exception e) {
                _pending = null;
                _cache.Record(null, DateTime.UtcNow);
                _next = DateTime.UtcNow.AddSeconds(_cache.Failures <= 3 ? 6 : 16);
                _last = (knownFromRawInput || _last.Receiver, null, e.GetType().Name);
                return Visible(_last);
            }
        }
        return Visible(_last);
    }

    private static async Task<(bool, int?, string)> QueryReceiver(string[] paths, bool known) {
        var info = new List<string>();
        foreach (var path in paths.Take(12)) {
            using var metadata = RazerReceiver.Open(path, 0);
            var product = RazerReceiver.Product(metadata);
            bool confirmed = KnownProduct(product) || known && string.IsNullOrWhiteSpace(product);
            if (!confirmed) continue;
            if (RazerReceiver.GetCaps(metadata) is not RazerReceiver.Caps caps) continue;
            if (caps.Page != 0xff60 || caps.Output is < 33 or > 128 || caps.Input is < 4 or > 128) {
                info.Add($"{caps.Page:X4}:{caps.Usage:X4} I={caps.Input} O={caps.Output}");
                continue;
            }
            using var handle = RazerReceiver.Open(path, 0xc0000000, overlapped: true);
            if (handle.IsInvalid) { info.Add("FF60 не открылся"); continue; }
            try {
                using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                string? rejected = null;
                for (int turn = 0; turn < 2; turn++) {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(turn == 0 ? 700 : 450));
                    byte[] reply = new byte[caps.Input];
                    var pendingRead = stream.ReadAsync(reply.AsMemory(), timeout.Token);
                    try {
                        await stream.WriteAsync(AulaBatteryPacket.Query(caps.Output).AsMemory(), timeout.Token);
                        for (int i = 0; i < 12; i++) {
                            int count = await pendingRead;
                            if (count <= 0) break;
                            if (AulaBatteryPacket.Percent(reply.AsSpan(0, count)) is int p) {
                                var status = AulaBatteryPacket.StatusFingerprint(reply.AsSpan(0, count));
                                return (true, p, $"{p}% · ответ ресивера" +
                                    (status is null ? "" : " · " + status));
                            }
                            if (AulaBatteryPacket.IsBatteryFrame(reply.AsSpan(0, count)))
                                rejected = AulaBatteryPacket.DescribeFrame(reply.AsSpan(0, count));
                            if (i < 11) pendingRead = stream.ReadAsync(reply.AsMemory(), timeout.Token);
                        }
                    } catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
                        // A busy receiver sometimes returns a zero frame first.
                        // Wait through it and, when needed, ask once more.
                    } finally {
                        timeout.Cancel();
                        try { await pendingRead; } catch (Exception) { }
                    }
                }
                info.Add(rejected is null ? "FF60 не вернул отчёт батареи" : $"FF60: ответ не прошёл проверку, {rejected}");
            } catch (OperationCanceledException) { info.Add("FF60: нет ответа"); }
            catch (IOException e) { info.Add($"FF60: ошибка {e.HResult:X8}"); }
        }
        return (known || info.Count > 0, null, info.Count == 0 ? "не найден канал FF60 с идентификатором F108Pro" : string.Join("; ", info));
    }
}
