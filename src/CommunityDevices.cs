namespace ChargeTray;

// A VID:PID identifies a USB interface, not a guarantee of a battery protocol.
// A generic dongle is listed with "Нет данных" until a verified reader responds.
internal static class CommunityDevices {
    internal static string? Label(HidInput.Input input) =>
        DeviceCatalog.TryGet(input.Hardware, out var p) ? p.Name :
        DeviceCatalog.IsPossibleWireless(input) ? input.Name + " (" + input.Hardware + ")" : null;
    internal static DeviceType Type(HidInput.Input input) {
        // Windows frequently exposes a single 2.4 GHz mouse receiver as both
        // Keyboard and Mouse. A stale user profile must not reintroduce the
        // incorrect role for hardware known to be a mouse.
        if (input.Hardware.Equals("25A7:FA07", StringComparison.OrdinalIgnoreCase) ||
            input.Hardware.Equals("25A7:FA08", StringComparison.OrdinalIgnoreCase) ||
            input.Hardware.Equals("1D57:FA60", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Mouse;
        return DeviceCatalog.TryGet(input.Hardware, out var profile)
            ? DeviceCatalog.Type(profile, input.Type) : input.Type;
    }
    internal static bool Known(HidInput.Input input) => Label(input) is not null;

    internal static Accessory[] Identify(IEnumerable<HidInput.Input> inputs, IReadOnlySet<string> ignoredHardware) =>
        inputs.Where(x => Known(x) && !ignoredHardware.Contains(x.Hardware))
            .GroupBy(x => x.Hardware, StringComparer.OrdinalIgnoreCase)
            .Select(group => {
                var x = group.First();
                var battery = group.Select(i => i.Percent).FirstOrDefault(p => p.HasValue);
                return new Accessory("community:" + group.Key, Label(x)!, Type(x),
                    battery.HasValue ? x.Transport : "USB / ресивер · заряд пока недоступен", battery);
            }).ToArray();
}
