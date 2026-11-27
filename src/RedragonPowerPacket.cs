namespace ChargeTray;

// Redragon/Areson (25A7:FA07) OEM 17-byte GetPower frame.
// For this receiver family the battery byte represents 10% steps.
// Never accept unknown status or malformed checksums as a battery level.
internal static class RedragonPowerPacket {
    internal const int Length = 17;
    internal static byte[] Query() {
        var frame = new byte[Length];
        frame[0] = 0x08;
        frame[1] = 0x04; // GetPower: no configuration fields are modified
        frame[^1] = Checksum(frame);
        return frame;
    }
    private static byte Checksum(ReadOnlySpan<byte> packet) {
        int sum = 0;
        for (int i = 0; i < Length - 1; i++) sum += packet[i];
        return unchecked((byte)(0x55 - sum));
    }
    internal static (int Percent, bool Charging)? Decode(ReadOnlySpan<byte> packet) {
        if (packet.Length != Length ||
            packet[0] is not (0x08 or 0x09) ||
            packet[1] != 0x04 || packet[2] != 0 ||
            packet[3] != 0 || packet[4] != 0 ||
            packet[5] < 1 || packet[^1] != Checksum(packet)) return null;
        int tenths = packet[6];
        if (tenths > 10) return null;
        bool charging = packet[5] >= 2 && packet[7] != 0;
        if (tenths == 0 && !charging) return null; // stale receiver reply
        return (tenths * 10, charging);
    }
}
