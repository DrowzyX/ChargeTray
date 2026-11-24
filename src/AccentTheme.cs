using System.Drawing;
using System.Globalization;

namespace ChargeTray;

// Theme colors are local user preferences, not hardware RGB lighting commands.
// The animated mode only redraws on-screen UI.
internal static class AccentTheme {
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChargeTray");
    private static readonly string SettingFile = Path.Combine(SettingsDir, "accent-v1.txt");
    private static Color _color = Color.FromArgb(226, 197, 160);
    private static bool _rgb;
    private static int _cycleSeconds = 9;
    internal static int CycleSeconds => _cycleSeconds;
    internal static Color Accent => _rgb ? Rainbow(Environment.TickCount64) : _color;
    internal static bool RgbEnabled => _rgb;
    internal static Color Fixed => _color;

    internal static void Load() {
        try {
            if (!File.Exists(SettingFile)) return;
            var lines = File.ReadAllLines(SettingFile);
            if (lines.Length == 0) return;
            if (TryParse(lines[0], out var color)) _color = color;
            _rgb = lines.Length > 1 && lines[1] == "rgb";
            if (lines.Length > 2 && int.TryParse(lines[2], out int seconds)) _cycleSeconds = Math.Clamp(seconds, 4, 30);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static bool TryParse(string value, out Color parsed) {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string h = value.Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return false;
        parsed = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        return true;
    }

    internal static void SetColor(Color value) {
        _color = Color.FromArgb(value.R, value.G, value.B);
        _rgb = false;
        Save();
    }
    internal static void SetRgb(bool enabled) { _rgb = enabled; Save(); }
    internal static void SetCycleSeconds(int seconds) { _cycleSeconds = Math.Clamp(seconds, 4, 30); Save(); }
    internal static void Reset() {
        _color = Color.FromArgb(226, 197, 160);
        _rgb = false;
        Save();
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllLines(SettingFile, new[] { $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}", _rgb ? "rgb" : "solid", _cycleSeconds.ToString() });
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static Color Rainbow(long milliseconds) {
        // Smooth ~9-second hue cycle, not rapid flashing.
        long duration = _cycleSeconds * 1000L;
        double hue = (milliseconds % duration) / (double)duration * 6.0;
        int section = (int)hue;
        double fraction = hue - section;
        double x = 1 - Math.Abs((hue % 2) - 1);
        // Soft pastel RGB on graphite UI; keep text contrast high.
        (double r, double g, double b) = section switch {
            0 => (1.0, x, 0.0), 1 => (x, 1.0, 0.0), 2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0), 4 => (x, 0.0, 1.0), _ => (1.0, 0.0, x)
        };
        return Color.FromArgb((int)(110 + r * 135), (int)(110 + g * 135), (int)(110 + b * 135));
    }
}
