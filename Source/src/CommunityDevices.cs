namespace ChargeTray;

// A VID/PID is an identifier of a USB interface, not proof of a battery protocol.
// Only publish percentages when the HID driver or a verified parser supplies them.
internal static class CommunityDevices {
    internal static string? Label(HidInput.Input input) => input.Hardware.ToUpperInvariant() switch {
        "28DE:1304" => "Steam Controller Wireless Receiver",
        "1E7D:30F7" => "ROCCAT Vulcan Pro",
        "258A:0049" => "Gaming Keyboard (258A:0049)",
        "1D57:FA60" => "Беспроводная мышь (1D57:FA60)",
        _ => null
    };
    internal static DeviceType Type(HidInput.Input input) => input.Hardware.ToUpperInvariant() switch {
        "28DE:1304" => DeviceType.Controller,
        "1E7D:30F7" or "258A:0049" => DeviceType.Keyboard,
        "1D57:FA60" => DeviceType.Mouse,
        _ => input.Type
    };
    internal static bool Known(HidInput.Input input) => Label(input) is not null;

    internal static Accessory[] Identify(IEnumerable<HidInput.Input> inputs, IReadOnlySet<string> ignoredHardware) =>
        inputs.Where(x => Known(x) && !ignoredHardware.Contains(x.Hardware))
            // Raw Input exposes some combo receivers as BOTH a keyboard and a mouse.
            .GroupBy(x => x.Hardware, StringComparer.OrdinalIgnoreCase)
            .Select(group => {
                var x = group.First();
                return new Accessory("community:" + group.Key, Label(x)!, Type(x),
                    x.Percent.HasValue ? x.Transport : "USB / ресивер · заряд пока недоступен",
                    group.Select(i => i.Percent).FirstOrDefault(p => p.HasValue));
            }).ToArray();
}
