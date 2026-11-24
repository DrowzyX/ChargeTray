using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChargeTray;

// Passive research assistant. No arbitrary HID output/feature reports,
// driver installation, process injection, packet capture or cloud uploads.
internal sealed record ProtocolDevice(string Label, string Type, string Connection,
    int? Percent, bool Charging, string Source, bool Stale);
internal sealed record ProtocolSnapshot(DateTime CapturedUtc,
    ProtocolDevice[] Devices, string[] HidMetadata);
internal sealed record ProtocolComparison(DateTime BeforeUtc, DateTime AfterUtc,
    string[] Changes, ProtocolSnapshot Before, ProtocolSnapshot After);

internal static class ProtocolLab {
    private static readonly Regex PathPattern = new(
        @"(?:\\\\[?]\\|HID\\)[^\s|]+", RegexOptions.IgnoreCase);
    private static readonly Regex MacPattern = new(
        @"\b(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b", RegexOptions.IgnoreCase);
    private static readonly Regex BleAddress = new(
        @"(?i)(?:bt:|bluetooth:)[0-9A-F:-]{10,}", RegexOptions.IgnoreCase);
    internal static ProtocolSnapshot Capture(IEnumerable<Accessory> devices,
        IEnumerable<string> diagnostics, DateTime atUtc) {
        var safeDevices = devices.Take(64).Select(d => new ProtocolDevice(
            Safe(d.Name, 90), d.Type.ToString(), Safe(d.Via, 100),
            d.Percent is >= 0 and <= 100 ? d.Percent : null,
            d.Charging, Safe(d.ReadingSource ?? "Windows / HID", 48), d.IsStale)).ToArray();
        // Reports consist of existing sanitized HID metadata and summaries;
        // trim further for public sharing. Do not include instance paths.
        var safeRows = diagnostics.Take(200).Select(r => Safe(r, 180)).ToArray();
        return new ProtocolSnapshot(atUtc, safeDevices, safeRows);
    }

    internal static ProtocolComparison Compare(ProtocolSnapshot before, ProtocolSnapshot after) {
        var changes = new List<string>();
        var old = before.Devices.GroupBy(d => d.Type + "|" + d.Label)
            .ToDictionary(g => g.Key, g => g.First());
        var newer = after.Devices.GroupBy(d => d.Type + "|" + d.Label)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var key in old.Keys.Union(newer.Keys).OrderBy(s => s, StringComparer.Ordinal)) {
            if (!newer.TryGetValue(key, out var current)) {
                changes.Add("Отключено: " + old[key].Label);
                continue;
            }
            if (!old.TryGetValue(key, out var previous)) {
                changes.Add("Подключено: " + current.Label);
                continue;
            }
            if (previous.Percent != current.Percent)
                changes.Add($"{current.Label}: {previous.Percent?.ToString() ?? "—"}% → {current.Percent?.ToString() ?? "—"}%");
            if (previous.Charging != current.Charging)
                changes.Add($"{current.Label}: статус зарядки {previous.Charging} → {current.Charging}");
            if (previous.Connection != current.Connection)
                changes.Add($"{current.Label}: {previous.Connection} → {current.Connection}");
            if (previous.Source != current.Source)
                changes.Add($"{current.Label}: источник {previous.Source} → {current.Source}");
        }
        foreach (var line in after.HidMetadata.Except(before.HidMetadata).Take(25))
            changes.Add("HID + " + line);
        foreach (var line in before.HidMetadata.Except(after.HidMetadata).Take(25))
            changes.Add("HID − " + line);
        if (changes.Count == 0) changes.Add("Заметных изменений в доступных метаданных нет.");
        return new ProtocolComparison(before.CapturedUtc, after.CapturedUtc,
            changes.ToArray(), before, after);
    }

    internal static string ExportJson(ProtocolComparison comparison) =>
        JsonSerializer.Serialize(comparison, new JsonSerializerOptions { WriteIndented = true });

    private static string Safe(string? text, int limit) {
        string value = (text ?? "").Replace("\r", " ").Replace("\n", " ");
        value = PathPattern.Replace(value, "[HID PATH REDACTED]");
        value = MacPattern.Replace(value, "[MAC REDACTED]");
        value = BleAddress.Replace(value, "[BLUETOOTH ID REDACTED]");
        return value.Length > limit ? value[..limit] : value;
    }
}
