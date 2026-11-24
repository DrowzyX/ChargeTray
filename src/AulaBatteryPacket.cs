namespace ChargeTray;

// AULA F108 Pro 2.4 GHz battery request, as documented by aula-driver.
internal static class AulaBatteryPacket {
    public static byte[] Query(int length) {
        if (length is < 33 or > 128) throw new ArgumentOutOfRangeException(nameof(length));
        var bytes = new byte[length];
        bytes[1] = 0x20;
        bytes[2] = 0x01;
        bytes[32] = (byte)(bytes[1] + bytes[2]);
        return bytes;
    }

    public static bool IsBatteryFrame(ReadOnlySpan<byte> bytes) {
        if (!bytes.IsEmpty && bytes[0] == 0) bytes = bytes[1..];
        return bytes.Length >= 2 && bytes[0] == 0x20 && bytes[1] == 0x01;
    }

    public static int? Percent(ReadOnlySpan<byte> bytes) {
        if (!IsBatteryFrame(bytes)) return null;
        // Windows prepends the zero HID report ID. The observed dongle
        // frame is 00 20 01 00 64 ... 85, where 64 hex means 100%.
        if (bytes[0] == 0) bytes = bytes[1..];
        if (bytes.Length < 32) return null;
        int sum = 0;
        for (int i = 0; i < 31; i++) sum += bytes[i];
        if ((sum & 255) != bytes[31]) return null;
        int percent = bytes[3];
        // A zero-only reply is indistinguishable from the echoed request.
        return percent is >= 1 and <= 100 ? percent : null;
    }

    // Diagnostic only: bytes following the validated battery percentage may
    // carry model-specific status, but no charging bit is currently confirmed.
    // Record a short, bounded fingerprint for controlled cable/no-cable tests.
    public static string? StatusFingerprint(ReadOnlySpan<byte> bytes) {
        if (Percent(bytes) is null) return null;
        if (bytes[0] == 0) bytes = bytes[1..];
        return "AULA status[4..11]=" + Convert.ToHexString(bytes.Slice(4, 8)) +
            " (raw; charging flag not verified)";
    }

    public static string DescribeFrame(ReadOnlySpan<byte> bytes) {
        var positions = new List<string>();
        for (int i = 0; i < Math.Min(64, bytes.Length); i++)
            if (bytes[i] != 0) positions.Add($"{i}={bytes[i]:X2}");
        return $"len={bytes.Length}, " + string.Join(" ", positions.Take(12));
    }
}
