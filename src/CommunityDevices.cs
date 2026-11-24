namespace ChargeTray;

// A VID:PID identifies a USB interface, not a guarantee of a battery protocol.
// A generic dongle is listed with "Нет данных" until a verified reader responds.
internal static class CommunityDevices {
    internal static string? Label(HidInput.Input input) =>
        DeviceCatalog.TryGet(input.Hardware, out var p) ? p.Name :
        DeviceCatalog.IsPossibleWireless(input) ? input.Name + " (" + input.Hardware + ")" : null;
    internal static DeviceType Type(HidInput.Input input) =>
        DeviceCatalog.TryGet(input.Hardware, out var p)
            ? DeviceCatalog.Type(p, input.Type) : input.Type;
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
