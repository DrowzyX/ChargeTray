namespace ChargeTray;

// Reject isolated startup frames, including false 100% immediately after reconnect.
internal sealed class XentaFrameConfirmation {
    private (byte Model, byte State, int Level)? _pending;
    private DateTime _pendingTime;
    private int _matches;

    internal int? Observe(ReadOnlySpan<byte> report, DateTime utcNow) {
        int? level = XentaBatteryPacket.Percent(report);
        if (level is null) return null;
        var frame = (Model: report[1], State: report[3], Level: level.Value);
        if (_pending != frame || utcNow - _pendingTime > TimeSpan.FromSeconds(12)) {
            _pending = frame;
            _matches = 1;
        } else {
            _matches++;
        }
        _pendingTime = utcNow;
        return _matches >= 2 ? level : null;
    }
}
