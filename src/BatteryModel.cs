namespace ChargeTray;

enum DeviceType { Keyboard, Mouse, Headphones, Controller, Other, SystemBattery }
enum Band { Empty, Low, Medium, Full }
sealed record Accessory(string Key, string Name, DeviceType Type, string Via,
    int? Percent = null, Band? Level = null, bool Charging = false, string? Model = null, string? Detail = null, bool IsStale = false) {
    public bool HasCurrentReading => !IsStale && (Percent.HasValue || Level.HasValue);
    public bool Low => !IsStale && !Charging && (Percent is <15 || Percent is null && Level is Band.Empty or Band.Low);
    public string Reading => Percent is int p ? IsStale ? $"≈{p}%" : $"{p}%" : Level switch {
        Band.Empty => "Почти пуст", Band.Low => "Низкий", Band.Medium => "Средний",
        Band.Full => "Высокий", _ => "Нет данных"
    };
}

sealed class LowBatteryAlerts {
    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);

    public Accessory[] Check(IEnumerable<Accessory> data) {
        var low = new List<Accessory>();
        foreach (var d in data) {
            if (d.IsStale) continue;
            if (d.Low && _alerted.Add(d.Key)) low.Add(d);
            if (d.Percent is >= 15 || (d.Percent is null && d.Level is Band.Medium or Band.Full))
                _alerted.Remove(d.Key);
        }
        return low.ToArray();
    }
}

sealed record GameReading(Accessory Device, ushort Vendor, ushort Product);

static class ControllerMerge {
    private static bool Xbox(Accessory d) => d.Type == DeviceType.Controller &&
        d.Name.Contains("Xbox", StringComparison.OrdinalIgnoreCase);

    private static Accessory Combine(Accessory device, Accessory game) => device with {
        Type = DeviceType.Controller,
        Percent = device.Percent ?? game.Percent,
        Level = device.Level ?? game.Level,
        Charging = device.Charging || game.Charging,
        Via = game.Percent.HasValue && !device.Percent.HasValue || game.Level.HasValue && !device.Percent.HasValue && !device.Level.HasValue
            ? device.Via + " + " + game.Via : device.Via
    };

    public static Accessory[] Merge(IEnumerable<Accessory> connected, IReadOnlyList<GameReading> native, IReadOnlyList<Accessory> xinput) {
        var result = connected.ToList();
        var btXbox = result.Where(d => d.Via == "Bluetooth" && Xbox(d)).ToArray();
        var used = new HashSet<string>();
        foreach (var game in native) {
            var d = game.Device;
            var names = result.Where(x => x.Via == "Bluetooth" && x.Type == DeviceType.Controller &&
                x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            Accessory? target = names.Length == 1 && native.Count(x => x.Device.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)) == 1
                ? names[0] : null;
            // An Xbox may have a generic Windows Gaming Input name. Merge only
            // when the Bluetooth and Microsoft/Xbox candidates are unambiguous.
            if (target is null && btXbox.Length == 1 && (Xbox(d) || game.Vendor == 0x045e) &&
                native.Count(x => Xbox(x.Device) || x.Vendor == 0x045e) == 1)
                target = btXbox[0];
            if (target is not null && used.Add(target.Key)) result[result.FindIndex(x => x.Key == target.Key)] = Combine(target, d);
            else result.Add(d);
        }

        if (xinput.Count == 1) {
            var probableXbox = result.Where(d => Xbox(d) || native.Any(g => g.Device.Key == d.Key && g.Vendor == 0x045e)).ToArray();
            if (probableXbox.Length == 1) {
                var d = probableXbox[0];
                // WGI can report a full capacity while the controller itself
                // reports it is low. Keep the reliable lower-resolution
                // reading instead of displaying a contradictory 100%.
                var xi = xinput[0];
                if (d.Percent == 100 && d.Via.Contains("Windows Gaming Input", StringComparison.Ordinal) &&
                    xi.Level is Band.Empty or Band.Low or Band.Medium)
                    d = d with { Percent = null };
                result[result.FindIndex(x => x.Key == d.Key)] = Combine(d, xi);
            } else if (native.Count == 0 && btXbox.Length == 0) result.Add(xinput[0]);
        } else if (native.Count == 0 && btXbox.Length == 0) result.AddRange(xinput);
        return result.ToArray();
    }

    public static int? CapacityPercent(bool hasBattery, int? remaining, int? full) {
        if (!hasBattery || remaining is not int value || full is not int capacity || value < 0 ||
            capacity <= 0 || value > capacity) return null;
        return (int)Math.Round(100.0 * value / capacity);
    }
}
