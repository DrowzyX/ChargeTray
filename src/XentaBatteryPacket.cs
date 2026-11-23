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
}
