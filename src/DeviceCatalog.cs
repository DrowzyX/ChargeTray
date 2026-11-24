using System.Text.Json;

namespace ChargeTray;

internal sealed class DeviceProfile {
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Mouse";
}
// Profiles only identify model/type: they do not invent battery protocols.
internal static class DeviceCatalog {
    private static readonly Dictionary<string, DeviceProfile> Builtin = new(StringComparer.OrdinalIgnoreCase) {
        ["28DE:1304"] = new() { Name = "Steam Controller Wireless Receiver", Type = "Controller" },
        ["1E7D:30F7"] = new() { Name = "ROCCAT Vulcan Pro", Type = "Keyboard" },
        ["258A:0049"] = new() { Name = "Gaming Keyboard (258A:0049)", Type = "Keyboard" },
        ["062A:38CC"] = new() { Name = "2.4G Keyboard/Mouse Receiver (062A:38CC)", Type = "Keyboard" },
        ["A8A5:2255"] = new() { Name = "YJX Wireless Mouse Receiver (A8A5:2255)", Type = "Mouse" },
        ["1D57:FA60"] = new() { Name = "Беспроводная мышь (1D57:FA60)", Type = "Mouse" },
        ["05AC:024F"] = new() { Name = "AULA F108 Pro", Type = "Keyboard" },
        ["1532:0083"] = new() { Name = "Razer Basilisk X HyperSpeed", Type = "Mouse" }
    };
    private static Dictionary<string, DeviceProfile> _profiles = new(Builtin, StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastLoaded;
    private static readonly string FilePath = Path.Combine(SettingsStore.DirectoryPath, "device-profiles.json");

    private static void EnsureLoaded() {
        if (DateTime.UtcNow - _lastLoaded < TimeSpan.FromMinutes(2)) return;
        _lastLoaded = DateTime.UtcNow;
        var profiles = new Dictionary<string, DeviceProfile>(Builtin, StringComparer.OrdinalIgnoreCase);
        try {
            if (File.Exists(FilePath)) {
                var custom = JsonSerializer.Deserialize<Dictionary<string, DeviceProfile>>(File.ReadAllText(FilePath));
                if (custom is not null)
                    foreach (var (key, profile) in custom) {
                        if (key.Length != 9 || key[4] != ':' ||
                            !key.Remove(4,1).All(Uri.IsHexDigit) ||
                            string.IsNullOrWhiteSpace(profile.Name) ||
                            profile.Name.Length > 90) continue;
                        profiles[key] = profile;
                    }
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        _profiles = profiles;
    }
    internal static bool TryGet(string hardware, out DeviceProfile profile) {
        EnsureLoaded();
        if (_profiles.TryGetValue(hardware, out var found)) { profile = found; return true; }
        profile = new DeviceProfile();
        return false;
    }
    internal static DeviceType Type(DeviceProfile profile, DeviceType fallback) =>
        Enum.TryParse<DeviceType>(profile.Type, true, out var value) ? value : fallback;
    internal static bool IsPossibleWireless(HidInput.Input input) {
        if (input.Transport != "USB / ресивер" || input.Hardware == "без VID:PID") return false;
        var n = input.Name;
        return n.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("2.4G", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("dongle", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("receiver", StringComparison.OrdinalIgnoreCase);
    }
}
