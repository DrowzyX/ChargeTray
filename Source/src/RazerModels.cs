namespace ChargeTray;

internal sealed record RazerModel(ushort Pid, string Name, byte Transaction) {
    internal string Hardware => $"1532:{Pid:X4}";
    internal bool Matches(string path) {
        string prefix = $@"\\?\hid#vid_1532&pid_{Pid:X4}";
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            path[prefix.Length] is '&' or '#';
    }
}

// Manufacturer/product IDs and transaction selectors are protocol data.
// Only these receiver IDs are eligible for the vendor battery read.
internal static class RazerModels {
    internal static readonly RazerModel[] Receivers = [
        new(0x0077, "Razer Pro Click", 0x1f),
        new(0x007b, "Razer Viper Ultimate", 0xff),
        new(0x007d, "Razer DeathAdder V2 Pro", 0x3f),
        new(0x0083, "Razer Basilisk X HyperSpeed", 0xff),
        new(0x0088, "Razer Basilisk Ultimate", 0x1f),
        new(0x0090, "Razer Naga Pro", 0x1f),
        new(0x0094, "Razer Orochi V2", 0x1f),
        new(0x009a, "Razer Pro Click Mini", 0x1f),
        new(0x009c, "Razer DeathAdder V2 X HyperSpeed", 0x1f),
        new(0x009f, "Razer Viper Mini Signature Edition", 0x1f),
        new(0x00a6, "Razer Viper V2 Pro", 0x1f),
        new(0x00a8, "Razer Naga V2 Pro", 0x1f),
        new(0x00ab, "Razer Basilisk V3 Pro", 0x1f),
        new(0x00b0, "Razer Cobra Pro", 0x1f),
        new(0x00b4, "Razer Naga V2 HyperSpeed", 0x1f),
        new(0x00b7, "Razer DeathAdder V3 Pro", 0x1f),
        new(0x00b8, "Razer Viper V3 HyperSpeed", 0x1f),
        new(0x00b9, "Razer Basilisk V3 X HyperSpeed", 0x1f),
        new(0x00bf, "Razer DeathAdder V4 Pro", 0x1f),
        new(0x00c1, "Razer Viper V3 Pro", 0x1f),
        new(0x00c3, "Razer DeathAdder V3 Pro", 0x1f),
        new(0x00c5, "Razer DeathAdder V3 HyperSpeed", 0x1f),
        new(0x00c8, "Razer Pro Click V2 Vertical Edition", 0x1f),
        new(0x00cd, "Razer Basilisk V3 Pro 35K", 0x1f),
        new(0x00d1, "Razer Pro Click V2", 0x1f),
        new(0x00d4, "Razer Basilisk Mobile", 0x1f),
        new(0x00d7, "Razer Basilisk V3 Pro 35K Phantom Green", 0x1f),
        new(0x00db, "Razer Cobra HyperSpeed", 0x1f),
        new(0x00df, "Razer Viper V3 Pro SE", 0x1f),
        new(0x00e0, "Razer Orochi V2 (2026)", 0x1f),
        new(0x00e6, "Razer Viper V4 Pro", 0x1f)
    ];
}
