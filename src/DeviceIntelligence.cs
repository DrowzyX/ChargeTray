using System.Text.Json;

namespace ChargeTray;

// Extensible, user-mode source contract. Native HID/Bluetooth readers remain
// authoritative by default; vendor bridges are explicitly opt-in.
// Future OEM adapters can implement IBatterySource without changing the UI.
internal interface IBatterySource {
    string Name { get; }
    IEnumerable<BatteryObservation> Read(DateTime utcNow);
}

internal sealed record BatteryObservation(
    string DeviceKey, int Percent, DateTime ObservedUtc, string Source,
    bool? Charging = null, int Confidence = 90);

internal static class BatterySourceResolver {
    // Reject stale or implausible readings. No percentage is inferred from VID:PID.
    internal static BatteryObservation? Choose(string key, IEnumerable<BatteryObservation> observations, DateTime now) =>
        observations.Where(r => string.Equals(key, r.DeviceKey, StringComparison.OrdinalIgnoreCase)
             && r.Percent is >= 0 and <= 100 && r.ObservedUtc.Kind == DateTimeKind.Utc
             && r.ObservedUtc <= now.AddSeconds(10) && now - r.ObservedUtc <= TimeSpan.FromMinutes(2)
             && r.Confidence is >= 1 and <= 100)
         .OrderByDescending(r => r.Confidence)
         .ThenByDescending(r => r.ObservedUtc)
         .FirstOrDefault();

    internal static Accessory[] Resolve(IEnumerable<Accessory> connected,
        IEnumerable<BatteryObservation> additional, DateTime now) {
        var list = additional.ToArray();
        return connected.Select(d => {
            var native = d.HasCurrentReading && d.Percent is int p
                ? new BatteryObservation(d.Key, p, now, NativeSource(d), d.Charging, 90)
                : null;
            var candidates = native is null ? list : list.Append(native);
            var winner = Choose(d.Key, candidates, now);
            if (winner is null)
                return d with { ReadingSource = d.IsStale ? "Последнее показание" : NativeSource(d) };
            // An optional vendor source is permitted only for a physically
            // detected device and only when its observation is fresh.
            return d with {
                Percent = winner.Percent, IsStale = false,
                Charging = winner.Charging ?? d.Charging,
                ReadingSource = winner.Source,
                ReadingAtUtc = winner.ObservedUtc,
                ReadingConfidence = winner.Confidence
            };
        }).ToArray();
    }

    private static string NativeSource(Accessory d) =>
        d.Via.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "Windows / Bluetooth" :
        d.Via.Contains("Донгл", StringComparison.OrdinalIgnoreCase) ? "HID / ресивер" :
        d.Via.Contains("USB", StringComparison.OrdinalIgnoreCase) ? "Windows / USB" : "Windows API";
}

// A vendor integration can supply readings from documented exports or IPC.
// This implementation consumes only a fixed local JSON file written by a
// user's trusted integration. It never reads vendor private files, injects
// into processes, opens arbitrary pipes or sends unverified HID commands.
internal sealed class VendorJsonSource : IBatterySource {
    internal static readonly string PathToFile = Path.Combine(
        SettingsStore.DirectoryPath, "vendor-bridge.json");
    public string Name => "Vendor Bridge (локальный JSON)";
    internal sealed class Payload {
        public int Version { get; set; } = 1;
        public BatteryObservation[] Readings { get; set; } = [];
    }
    public IEnumerable<BatteryObservation> Read(DateTime utcNow) {
        try {
            var info = new FileInfo(PathToFile);
            if (!info.Exists || info.Length > 65536 || info.Length == 0) return [];
            // No network requests, paths or commands are accepted.
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(PathToFile));
            if (payload is null || payload.Version != 1) return [];
            return (payload.Readings ?? []).Take(64)
                .Where(r => !string.IsNullOrWhiteSpace(r.DeviceKey) && r.DeviceKey.Length <= 120
                    && r.Source == "Vendor Bridge" && r.Confidence is >= 1 and <= 95
                    && r.ObservedUtc.Kind == DateTimeKind.Utc
                    && r.ObservedUtc <= utcNow.AddSeconds(10)
                    && utcNow - r.ObservedUtc <= TimeSpan.FromMinutes(2)
                    && r.Percent is >= 0 and <= 100).ToArray();
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException) { return []; }
    }
}
