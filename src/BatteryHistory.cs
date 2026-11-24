using System.Text.Json;

namespace ChargeTray;

// Local-only, hourly samples. No serial numbers, telemetry, or cloud access.
internal sealed class BatteryHistory {
    internal sealed record Sample(string Key, DateTime Utc, int Percent, bool Charging);
    private readonly List<Sample> _items = [];
    private readonly string _path = Path.Combine(SettingsStore.DirectoryPath, "history-v2.json");
    internal BatteryHistory() {
        try {
            if (File.Exists(_path))
                _items.AddRange(JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText(_path)) ?? []);
        } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        Prune();
    }
    internal void Observe(IEnumerable<Accessory> devices) {
        bool changed = false;
        DateTime now = DateTime.UtcNow;
        foreach (var d in devices) {
            if (d.IsStale || d.Percent is not int level) continue;
            var last = _items.LastOrDefault(s => s.Key == d.Key);
            if (last is not null && now - last.Utc < TimeSpan.FromMinutes(30)) continue;
            _items.Add(new(d.Key, now, level, d.Charging));
            changed = true;
        }
        if (!changed) return;
        Prune();
        try {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_items));
            File.Move(_path + ".tmp", _path, overwrite: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private void Prune() {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        _items.RemoveAll(s => s.Utc < cutoff || s.Percent is < 0 or > 100);
        if (_items.Count > 12000) _items.RemoveRange(0, _items.Count - 12000);
    }
    internal Sample[] Samples(string key, int days = 7) =>
        _items.Where(s => s.Key == key && s.Utc >= DateTime.UtcNow.AddDays(-days))
            .OrderBy(s => s.Utc).ToArray();

    internal string Forecast(string key) {
        var recent = Samples(key, 14).Where(x => !x.Charging).ToArray();
        if (recent.Length < 4) return "Недостаточно измерений для прогноза";
        // Only use a continuous downtrend in the latest four observations.
        var tail = recent.TakeLast(Math.Min(12, recent.Length)).ToArray();
        if (tail.Any(x => x.Percent > tail[0].Percent + 2)) return "Нестабильные данные: прогноз недоступен";
        var first = tail[0];
        var last = tail[^1];
        var hours = (last.Utc - first.Utc).TotalHours;
        int drop = first.Percent - last.Percent;
        if (hours < 4 || drop < 3 || hours > 336) return "Недостаточно достоверного снижения заряда";
        var estimate = last.Percent * hours / drop;
        if (estimate > 24 * 90) return "Расход слишком мал для оценки";
        return estimate >= 24 ? $"Примерно {estimate / 24:0.#} дн. до 0% (оценка)" :
            $"Примерно {estimate:0.#} ч до 0% (оценка)";
    }
}
