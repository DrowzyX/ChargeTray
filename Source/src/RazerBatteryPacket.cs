namespace ChargeTray;

// Wire format cross-checked against OpenRazer (razermouse_driver.c) and
// HaloBattery's Windows HID protocol notes. This is a read-only battery query.
internal static class RazerBatteryPacket {
    public const int Length = 91;

    public static byte[] Query(byte transaction = 0xff) {
        byte[] packet = new byte[Length];
        packet[2] = transaction;
        packet[6] = 0x02;
        packet[7] = 0x07;
        packet[8] = 0x80;
        for (int i = 3; i < 89; i++) packet[89] ^= packet[i];
        return packet;
    }

    // Ignore responses to other software's commands, and always ignore a
    // busy, failed, or asleep status even when it contains a data byte.
    public static int? Percent(ReadOnlySpan<byte> reply) =>
        reply.Length == Length && reply[0] == 0 && reply[1] == 0x02 &&
        reply[7] == 0x07 && reply[8] == 0x80 ? (reply[10] * 100 + 127) / 255 : null;
}
