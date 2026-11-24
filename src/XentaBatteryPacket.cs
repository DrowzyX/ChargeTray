namespace ChargeTray;

// Passive reports emitted by SOME 1D57:FA60 receivers. This parser does NOT
// identify the model. It must only be applied to the vendor's battery collection.
internal static class XentaBatteryPacket {
    internal static int? Percent(ReadOnlySpan<byte> report) {
        // The incoming buffer should include the report ID 03.
        if (report.Length != 5 || report[0] != 0x03 || report[2] != 0x40 ||
            report[3] is not (0x01 or 0x02 or 0x03)) return null;
        int value = report[4];
        // Some variants emit unknown/zero reports. Don't notify on bogus 0%.
        if (value is < 1 or > 100) return null;
        // Model-specific scaling requires an independently validated model code.
        return value;
    }
    // Explain invalid candidate frames without treating model bytes or status
    // fields as percentages. These observations are for diagnosis only.
    internal static string RejectionReason(ReadOnlySpan<byte> report) {
        if (report.Length != 5) return "длина HID-отчёта не равна 5 байтам";
        if (report[0] != 0x03) return "неизвестный Report ID";
        if (report[2] != 0x40) return "не является пакетом батареи (маркер != 40)";
        if (report[3] is not (0x01 or 0x02 or 0x03))
            return $"неподтверждённое состояние ST=0x{report[3]:X2}, поле LL=0x{report[4]:X2} не используется как процент";
        if (report[4] is < 1 or > 100)
            return $"неподтверждённый уровень LL=0x{report[4]:X2}";
        return "валидный кандидат батарейного отчёта, ожидаем повторного подтверждения";
    }

}
