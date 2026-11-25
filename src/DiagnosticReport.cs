using System.Text;
using System.Text.RegularExpressions;

namespace ChargeTray;

// No raw instance paths, serial numbers, Bluetooth addresses or user IDs in reports.
internal static class DiagnosticReport {
    internal static string Summary(string version, string os, DateTime? refreshed,
        IEnumerable<Accessory> devices, IEnumerable<string> errors) {
        var b = new StringBuilder().AppendLine($"ChargeTray {version}")
            .AppendLine(os).AppendLine($"Обновлено: {refreshed?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}")
            .AppendLine("Устройства:");
        int count = 0;
        foreach (var d in devices) {
            count++;
            b.Append("• ").Append(d.Name).Append(" | ").Append(d.Type)
                .Append(" | ").Append(d.Via).Append(" | ").AppendLine(d.Reading);
        }
        if (count == 0) b.AppendLine("• Нет обнаруженных устройств");
        foreach (var e in errors.Distinct()) b.AppendLine("Ошибка: " + e);
        b.AppendLine().AppendLine("Для подключения новой модели используйте «Скопировать отчёт для разработчика».");
        return b.ToString();
    }

    internal static string Developer(string version, string os, DateTime? refreshed,
        IEnumerable<Accessory> devices, IEnumerable<string> hardwareLines, IEnumerable<string> errors,
        IEnumerable<HidInput.Input> inputs, IEnumerable<(string Path, RazerReceiver.Caps? Caps)> collections,
        string? samplingJournal = null) {
        var b = new StringBuilder(Summary(version, os, refreshed, devices, errors))
            .AppendLine().AppendLine("=== USB / HID (без серийных номеров и путей) ===");
        var groups = inputs.GroupBy(d => d.Hardware == "без VID:PID" ? d.Hardware + ":" + d.Name : d.Hardware,
            StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups) {
            var first = group.First();
            var roles = string.Join("/", group.Select(x => x.Type.ToString()).Distinct());
            var names = string.Join(" / ", group.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
            b.Append("• ").Append(first.Hardware).Append(" | ").Append(names)
                .Append(" | интерфейсы: ").Append(group.Count())
                .Append(" | роли Windows: ").Append(roles)
                .Append(" | ").AppendLine(first.Transport);
        }
        b.AppendLine().AppendLine("=== HID коллекции ===");
        int n = 0;
        foreach (var (path, caps) in collections) {
            var id = Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
            if (!id.Success) continue;
            string hw = (id.Groups[1].Value + ":" + id.Groups[2].Value).ToUpperInvariant();
            string iface = Regex.Match(path, @"&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
            string col = Regex.Match(path, @"&col([0-9a-f]{2})", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
            b.Append("• ").Append(hw).Append(" MI=").Append(iface.Length > 0 ? iface : "—")
                .Append(" COL=").Append(col.Length > 0 ? col : "—");
            if (caps is { } c) b.Append($" usage={c.Page:X4}:{c.Usage:X4} IN={c.Input} OUT={c.Output} FEATURE={c.Feature}");
            else b.Append(" caps=unavailable");
            b.AppendLine();
            n++;
        }
        if (n == 0) b.AppendLine("Нет доступных HID коллекций");
        b.AppendLine().AppendLine("=== Протоколы батареи ===");
        var protocol = hardwareLines.Where(x => x.StartsWith("AULA ", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("Razer ", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("Xenta ", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        foreach (var line in protocol) b.AppendLine("• " + line);
        if (protocol.Length == 0) b.AppendLine("Нет подробных ответов от ресиверов");
        var metadata = collections.Select(pair => {
            var id = Regex.Match(pair.Path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
            if (!id.Success || pair.Caps is not { } c) return null;
            string hw = (id.Groups[1].Value + ":" + id.Groups[2].Value).ToUpperInvariant();
            var mi = Regex.Match(pair.Path, @"&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase).Groups[1].Value;
            var col = Regex.Match(pair.Path, @"&col([0-9a-f]{2})", RegexOptions.IgnoreCase).Groups[1].Value;
            return new HidCompatibilityHints.Interface(hw, mi.Length > 0 ? mi.ToUpperInvariant() : "—",
                col.Length > 0 ? col.ToUpperInvariant() : "—",
                c.Page, c.Usage, c.Input, c.Output, c.Feature);
        }).Where(x => x is not null).Cast<HidCompatibilityHints.Interface>().ToArray();
        b.AppendLine().AppendLine(HidCompatibilityHints.Describe(metadata));
        if (!string.IsNullOrWhiteSpace(samplingJournal))
            b.AppendLine().AppendLine(samplingJournal);
        b.AppendLine().AppendLine("Отчёт использует только метаданные HID. Не отправляйте серийные номера, MAC-адреса или сырые пути устройств.");
        return b.ToString();
    }
}
