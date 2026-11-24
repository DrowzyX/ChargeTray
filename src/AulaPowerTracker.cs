namespace ChargeTray;

// Conservative evidence-based estimate for keyboards whose receiver only
// exposes a battery percentage. Never used as a confirmed charging flag.
internal sealed class AulaPowerTracker {
    private int? _previousPercent;
    private DateTime _previousUtc;
    private int _rises;
    private DateTime _lastRiseUtc;
    private DateTime _firstRiseUtc;

    internal bool LikelyCharging { get; private set; }

    internal void Observe(int? percent, bool stale, DateTime utcNow) {
        if (stale || percent is null || percent < 1 || percent > 100) return;

        // Prevent stale cached readings and repeated UI polls from accumulating evidence.
        if (_previousPercent is null || utcNow - _previousUtc > TimeSpan.FromHours(2)) {
            _previousPercent = percent;
            _previousUtc = utcNow;
            _rises = 0;
            LikelyCharging = false;
            return;
        }

        int delta = percent.Value - _previousPercent.Value;
        var interval = utcNow - _previousUtc;
        if (delta < 0 || delta > 5) {
            // Falling battery or a sudden jump (reconnection/calibration)
            // invalidates the previous inference.
            _rises = 0;
            LikelyCharging = false;
        } else if (delta > 0 && interval >= TimeSpan.FromSeconds(30)) {
            if (_rises == 0 || utcNow - _lastRiseUtc > TimeSpan.FromMinutes(45)) {
                _rises = 1;
                _firstRiseUtc = utcNow;
            } else {
                _rises++;
            }
            _lastRiseUtc = utcNow;
            if (_rises >= 2 && utcNow - _firstRiseUtc >= TimeSpan.FromMinutes(1))
                LikelyCharging = true;
        }
        if (_lastRiseUtc != default && utcNow - _lastRiseUtc > TimeSpan.FromMinutes(30)) {
            _rises = 0;
            LikelyCharging = false;
        }
        if (percent == 100) LikelyCharging = false;
        _previousPercent = percent;
        _previousUtc = utcNow;
    }

    internal void Reset() {
        _previousPercent = null;
        _previousUtc = default;
        _lastRiseUtc = default;
        _firstRiseUtc = default;
        _rises = 0;
        LikelyCharging = false;
    }
}
