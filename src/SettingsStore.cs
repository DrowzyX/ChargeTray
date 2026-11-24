using System.Text.Json;

namespace ChargeTray;

internal sealed class DevicePreferences {
    public string Name { get; set; } = "";
    public bool Pinned { get; set; }
    public int AlertThreshold { get; set; } = 15;
}
internal sealed class AppPreferences {
    public bool ConnectionNotifications { get; set; } = true;
    public bool BatteryNotifications { get; set; } = true;
    public bool BatteryHistory { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public bool EconomyMode { get; set; }
    public bool EnableVendorBridge { get; set; } = false;
    public string ThemeMode { get; set; } = "dark";
    public int IslandMonitor { get; set; } = 0;
    public int IslandTopOffset { get; set; } = 9;
    public Dictionary<string, DevicePreferences> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
internal sealed class SettingsStore {
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChargeTray");
    internal static readonly string FilePath = Path.Combine(DirectoryPath, "preferences-v2.json");
    internal AppPreferences Values { get; private set; } = new();
    internal SettingsStore() { Load(); }
    internal void Load() {
        try {
            if (!File.Exists(FilePath)) return;
            Values = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? new();
            Values.Devices = new Dictionary<string, DevicePreferences>(
                Values.Devices ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (var p in Values.Devices.Values) p.AlertThreshold = Math.Clamp(p.AlertThreshold, 1, 99);
            Values.IslandMonitor = Math.Max(0, Values.IslandMonitor);
            Values.IslandTopOffset = Math.Clamp(Values.IslandTopOffset, 0, 120);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            Values = new(); // Corrupted preferences must never block startup.
        }
    }
    internal DevicePreferences Device(string key) {
        if (!Values.Devices.TryGetValue(key, out var value))
            Values.Devices[key] = value = new();
        return value;
    }
    internal Accessory[] Apply(IEnumerable<Accessory> devices) =>
        devices.Select(d => {
            if (!Values.Devices.TryGetValue(d.Key, out var p) || string.IsNullOrWhiteSpace(p.Name))
                return d;
            return d with { Name = p.Name.Trim() };
        }).OrderByDescending(d => Values.Devices.TryGetValue(d.Key, out var p) && p.Pinned)
          .ThenBy(d => d.Type).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    internal int Threshold(string key) => Values.Devices.TryGetValue(key, out var p)
        ? Math.Clamp(p.AlertThreshold, 1, 99) : 15;
    internal void Save() {
        try {
            Directory.CreateDirectory(DirectoryPath);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
