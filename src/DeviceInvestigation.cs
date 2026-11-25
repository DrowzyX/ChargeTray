using System.Text;
using System.Text.RegularExpressions;

namespace ChargeTray;

// Lightweight in-memory history: collects ONLY the existing interpreted values.
// No raw keyboard/mouse input, HID paths, serials, MACs or USB packet interception.
internal sealed class DeviceInvestigation {
    private readonly Queue<Sample> _samples = new();
    internal sealed record Reading(string Model, string Type, string Connection, string Battery,
        string Source, bool Stale, bool UsbConnected, bool Charging);
    internal sealed record Sample(DateTime AtUtc, Reading[] Devices, string[] ErrorTypes);
    internal int Count => _samples.Count;
    private static string Safe(string value, int length = 85) {
        string v = Regex.Replace(value.Replace('\r', ' ').Replace('\n', ' '),
            @"(?i)(?:\\\\[?]\\|HID\\)[^\s|]+|\b(?:[a-f0-9]{2}[:-]){5}[a-f0-9]{2}\b",
            "[REDACTED]");
        return v.Length > length ? v[..length] : v;
    }
    internal void Observe(IEnumerable<Accessory> devices, IEnumerable<string> errors, DateTime utc) {
        var readings = devices.Take(48).Select(d => new Reading(
            Safe(d.Name), d.Type.ToString(), Safe(d.Via),
            d.Percent is >= 0 and <= 100 ? d.Percent + "%" : "Нет данных",
            Safe(d.ReadingSource ?? "Windows/HID", 45), d.IsStale,
            d.UsbConnected, d.Charging)).ToArray();
        // Do not retain exception messages containing arbitrary paths.
        var snapshot = new Sample(utc, readings,
            errors.Select(e => Safe(e.Split(':')[0], 50)).Take(8).ToArray());
        _samples.Enqueue(snapshot);
        while (_samples.Count > 36) _samples.Dequeue();
    }
    internal string Report() {
        var b = new StringBuilder()
            .AppendLine("=== Автоматический журнал опросов (в оперативной памяти) ===")
            .AppendLine("Только уже декодированные показатели; сырые HID-пакеты и нажатия не записываются.")
            .AppendLine("Последние 36 опросов, история удаляется при выходе из ChargeTray.");
        if (_samples.Count == 0) return b.AppendLine("Опросов пока нет.").ToString();
        var samples = _samples.ToArray();
        b.AppendLine($"Опросов: {samples.Length}, первый: {samples[0].AtUtc.ToLocalTime():HH:mm:ss}, последний: {samples[^1].AtUtc.ToLocalTime():HH:mm:ss}");
        foreach (var snap in samples) {
            b.AppendLine($"[{snap.AtUtc.ToLocalTime():HH:mm:ss}] устройств: {snap.Devices.Length}");
            foreach (var d in snap.Devices)
                b.Append("  ").Append(d.Model).Append(" (").Append(d.Type).Append(")")
                    .Append(": ").Append(d.Battery).Append(d.Stale ? " · последнее показание" : "")
                    .Append(d.UsbConnected ? " · USB подключён" : "")
                    .Append(d.Charging ? " · зарядка подтверждена" : "")
                    .Append(" · ").Append(d.Source).AppendLine();
            foreach (var error in snap.ErrorTypes)
                b.AppendLine("  Тип ошибки: " + error);
        }
        return b.ToString();
    }
}

internal static class HidCompatibilityHints {
    internal sealed record Interface(string Hardware, string Mi, string Col,
        ushort Page, ushort Usage, int Input, int Output, int Feature);
    internal static string Describe(IEnumerable<Interface> items) {
        var b = new StringBuilder().AppendLine("=== Возможности HID и пути дальнейшей поддержки ===");
        var groups = items.Take(128).GroupBy(x => x.Hardware, StringComparer.OrdinalIgnoreCase);
        if (!groups.Any()) return b.AppendLine("HID-коллекции не обнаружены.").ToString();
        foreach (var g in groups) {
            var vendor = g.Where(x => x.Page >= 0xFF00).ToArray();
            var standard = g.Where(x => x.Page < 0xFF00).ToArray();
            b.AppendLine("• " + g.Key + ": коллекций " + g.Count() +
                ", vendor-specific " + vendor.Length + ", стандартных " + standard.Length);
            foreach (var x in vendor.Take(12)) {
                b.AppendLine($"  MI={x.Mi} COL={x.Col} usage={x.Page:X4}:{x.Usage:X4} IN={x.Input} OUT={x.Output} FEATURE={x.Feature}");
            }
            if (vendor.Length > 0)
                b.AppendLine("  Возможен фирменный протокол. Длина FEATURE/INPUT не раскрывает значение батареи; требуется проверенный ответ производителя.");
            else b.AppendLine("  Фирменных HID-коллекций не найдено. Проверяйте Windows API / Bluetooth / OEM-службу.");
            if (g.Key.Equals("25A7:FA07", StringComparison.OrdinalIgnoreCase))
                b.AppendLine("  Семейство Redragon/Areson: исследовать 17-байтовую FEATURE-коллекцию. Запросы без проверенного профиля не отправляются.");
            if (g.Key.Equals("1D57:FA60", StringComparison.OrdinalIgnoreCase))
                b.AppendLine("  Мышь 1D57:FA60: полученные статусы F3/73 не подтверждают процент. Нужна корреляция с фирменным ПО.");
            if (g.Key.Equals("258A:0049", StringComparison.OrdinalIgnoreCase))
                b.AppendLine("  Клавиатура 258A:0049: отмечены vendor FEATURE-коллекции; содержимое не читается автоматически во избежание побочных эффектов.");
        }
        b.AppendLine("Чтение неизвестных FEATURE/OUTPUT, USB-сниффинг и запись нажатий отключены.");
        return b.ToString();
    }
}
