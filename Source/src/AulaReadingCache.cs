namespace ChargeTray;

internal sealed class AulaReadingCache {
    private DateTime _lastAt;
    private int? _lastPercent;
    internal int Failures { get; private set; }

    internal void Record(int? percent, DateTime now) {
        if (percent is int p) { _lastAt = now; _lastPercent = p; Failures = 0; }
        else Failures = Math.Min(Failures + 1, 4);
    }

    internal (int? Percent, bool Stale) Display(int? fresh, DateTime now) {
        if (fresh.HasValue) return (fresh, false);
        return _lastPercent.HasValue && now - _lastAt <= TimeSpan.FromMinutes(2) ? (_lastPercent, true) : (null, false);
    }
}
