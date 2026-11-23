using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.System.Power;
using Windows.Gaming.Input;
using Windows.Storage.Streams;

namespace ChargeTray;

sealed class BatteryReader {
    internal readonly List<string> Errors = new();
    internal string[] HardwareDiagnostics { get; private set; } = [];
    private readonly RazerReceiver[] _razer = RazerModels.Receivers.Select(m => new RazerReceiver(m)).ToArray();
    private readonly AulaF108 _aula = new();
    private readonly XentaReceiver _xenta = new();
    internal bool RetryAulaSoon => _aula.RetrySoon;
    private readonly Dictionary<string, (DateTime Next, int? Value, string? Model)> _ble = new();
    private static readonly SemaphoreSlim RadioReads = new(3);
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string Address = "System.Devices.Aep.DeviceAddress";
    private const string Container = "System.Devices.Aep.ContainerId";
    private const string Class = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string Minor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private static readonly string[] Props = [Address, Container, Class, Minor, Appearance, BatteryKey, "System.Devices.ModelName", IsConnected];

    public async Task<Accessory[]> Read(bool force) {
        Errors.Clear();
        HardwareDiagnostics = [];
        HidInput.Input[] hid = [];
        try { hid = HidInput.Read(); }
        catch (Exception e) { Errors.Add($"HID: {e.GetType().Name}"); }
        var razerPaths = RazerReceiver.PresentInterfaces(p => p.StartsWith(@"\\?\hid#vid_1532&pid_", StringComparison.OrdinalIgnoreCase));
        var razerTasks = _razer.Select(async r => {
            try { return (r.Model, Result: await r.Read(razerPaths, force)); }
            catch { return (r.Model, Result: (Receiver: false, Percent: (int?)null, Details: "")); }
        }).ToArray();
        var aulaTask = _aula.Read(force, hid.Any(AulaF108.IsKnownInput));
        var xentaTask = _xenta.Read(force);
        var classic = BluetoothDevices(false, force);
        var ble = BluetoothDevices(true, force);
        await Task.WhenAll(classic, ble);
        var groups = (await ble).Concat(await classic)
            .GroupBy(d => DeviceAddress(d.Info) is string address ? "bt:" + address :
                d.Info.Properties.TryGetValue(Container, out var v) && v is Guid g && g != Guid.Empty ? "bt:" + g :
                "bt:" + d.Info.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var nodes = groups.Count > 0 ? PnpBattery.Read() : new Dictionary<string, int>();
        var connected = new List<Accessory>();
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(groups.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _ble.Keys.Where(k => !seen.Contains(k)).ToArray()) _ble.Remove(key);
        var readings = await Task.WhenAll(groups.Select(async group => {
            var first = group.First();
            var info = first.Info;
            var name = group.Select(d => d.Info.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Bluetooth-устройство";
            var model = group.Select(d => Property(d.Info, "System.Devices.ModelName") as string)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            int? os = group.Select(d => Number(Property(d.Info, BatteryKey))).FirstOrDefault(n => n.HasValue);
            if (os is null && DeviceAddress(info) is string addr && nodes.TryGetValue(addr, out var battery)) os = battery;
            var endpoint = group.FirstOrDefault(d => d.Le);
            bool update = endpoint.Info is not null && (force || !_ble.TryGetValue(group.Key, out var cache) || cache.Next <= now);
            (int? Value, string? Model) radio = update ? await ReadBle(endpoint.Info!.Id, model is null && (!_ble.TryGetValue(group.Key, out var old) || old.Model is null))
                : _ble.TryGetValue(group.Key, out var stored) ? (stored.Value, stored.Model) : (null, null);
            return (group.Key, Device: new Accessory(group.Key, name, Kind(name, group.Select(d => d.Info)),
                "Bluetooth", radio.Value ?? os, Model: model ?? radio.Model),
                Update: update, radio.Value, radio.Model);
        }));
        foreach (var item in readings) {
            if (item.Update) {
                _ble.TryGetValue(item.Key, out var prev);
                _ble[item.Key] = (DateTime.UtcNow.AddSeconds(item.Value.HasValue ? 60 : 120), item.Value, item.Model ?? prev.Model);
            }
            var itemDevice = item.Device;
            if (itemDevice.Model is null && _ble.TryGetValue(item.Key, out var saved)) itemDevice = itemDevice with { Model = saved.Model };
            connected.Add(itemDevice);
        }
        var razers = (await Task.WhenAll(razerTasks)).Where(x => x.Result.Receiver).ToArray();
        var razerIds = razers.Select(r => r.Model.Hardware).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aula = (Receiver: false, Percent: (int?)null, Details: "", Stale: false);
        try { aula = await aulaTask; }
        catch (Exception e) { Errors.Add($"AULA HID: {e.GetType().Name}"); }
        HardwareDiagnostics = hid.Select(x => x.Diagnostic).Distinct().ToArray();
        var xenta = await xentaTask;
        if (xenta.Found) HardwareDiagnostics = [..HardwareDiagnostics, $"Xenta 2.4 ГГц | 1D57:FA60 | {xenta.Details}"];
        var reserved = razerIds.Concat(aula.Receiver ? new[] { AulaF108.Id } : Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (xenta.Found) reserved.Add("1D57:FA60");
        connected.AddRange(CommunityDevices.Identify(hid, reserved));
        if (xenta.Found) connected.Add(new("xenta:1d57:fa60", "Беспроводная мышь (1D57:FA60)", DeviceType.Mouse,
            xenta.Level.HasValue ? "Донгл 2,4 ГГц" : "Донгл найден · нет подтверждённого заряда", xenta.Level));
        connected.AddRange(hid.Where(x => x.Transport != "Bluetooth HID" && x.Percent.HasValue &&
            !razerIds.Contains(x.Hardware) && !CommunityDevices.Known(x) &&
            !(aula.Receiver && AulaF108.IsKnownInput(x)))
            .Select(x => new Accessory("hid:" + x.Instance, x.Name, x.Type, x.Transport, x.Percent)));
        foreach (var (model, razer) in razers) {
            HardwareDiagnostics = [..HardwareDiagnostics, $"Razer 2.4 ГГц | {model.Name} | {model.Hardware} | {razer.Details}"];
            int? level = razer.Percent ?? hid.FirstOrDefault(x => x.Hardware == model.Hardware && x.Percent.HasValue)?.Percent;
            connected.Add(new("razer:" + model.Hardware, model.Name, DeviceType.Mouse,
                level.HasValue ? "Донгл 2,4 ГГц" : "Донгл найден · нет ответа мыши", level));
        }
        if (aula.Receiver) {
            HardwareDiagnostics = [..HardwareDiagnostics, $"AULA 2.4 ГГц | {AulaF108.Name} | {AulaF108.Id} | {aula.Details}"];
            int? level = aula.Percent ?? hid.FirstOrDefault(x => AulaF108.IsKnownInput(x) && x.Percent.HasValue)?.Percent;
            connected.Add(new("aula:f108-pro", AulaF108.Name, DeviceType.Keyboard,
                aula.Stale ? "Донгл 2,4 ГГц · последнее показание" :
                level.HasValue ? "Донгл 2,4 ГГц" : "Донгл найден · нет показаний", level, IsStale: aula.Stale));
        }
        if (WindowsBattery.Read() is { } batteryDevice) connected.Add(batteryDevice);
        return MergeControllers(connected).OrderBy(d => d.Type).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task<(DeviceInformation Info, bool Le)[]> BluetoothDevices(bool le, bool force) {
        try {
            var selector = le ? BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected) :
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            DeviceInformationCollection devices;
            try {
                devices = await DeviceInformation.FindAllAsync(selector, Props, DeviceInformationKind.AssociationEndpoint).AsTask(timeout.Token);
            } catch (Exception) when (!timeout.IsCancellationRequested) {
                devices = await DeviceInformation.FindAllAsync(selector, new[] { Address }, DeviceInformationKind.AssociationEndpoint).AsTask(timeout.Token);
            }
            if (!force) return devices.Select(d => (d, le)).ToArray();
            try {
                // Manual refresh checks a second Windows view. It does not
                // connect to sleeping or previously paired devices.
                var pairedSelector = le ? BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) :
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                var paired = await DeviceInformation.FindAllAsync(pairedSelector, Props, DeviceInformationKind.AssociationEndpoint)
                    .AsTask(timeout.Token);
                return devices.Concat(paired.Where(d => Property(d, IsConnected) is true)).DistinctBy(d => d.Id)
                    .Select(d => (d, le)).ToArray();
            } catch (Exception) { return devices.Select(d => (d, le)).ToArray(); }
        } catch (Exception e) { lock (Errors) Errors.Add($"Bluetooth {(le ? "LE" : "Classic")}: {e.GetType().Name}"); return []; }
    }

    private static object? Property(DeviceInformation d, string name) => d.Properties.TryGetValue(name, out var value) ? value : null;
    private static int? Number(object? value, int max = 100) => value is not null && int.TryParse(value.ToString(), out int p) && p >= 0 && p <= max ? p : null;
    private static string? DeviceAddress(DeviceInformation d) {
        if (Property(d, Address) is string raw) {
            var digits = raw.Replace(":", "").Replace("-", "");
            if (digits.Length == 12 && digits.All(Uri.IsHexDigit)) return digits.ToUpperInvariant();
        }
        var match = Regex.Match(d.Id, @"[0-9a-f]{2}(?::[0-9a-f]{2}){5}$", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.Replace(":", "").ToUpperInvariant() : null;
    }

    private static DeviceType Kind(string name, IEnumerable<DeviceInformation> devices) {
        foreach (var d in devices) {
            var minor = Number(Property(d, Minor), 255);
            var major = Number(Property(d, Class), 8192);
            var appearance = Number(Property(d, Appearance), 65535);
            if (appearance == 961) return DeviceType.Keyboard;
            if (appearance == 962) return DeviceType.Mouse;
            if (appearance is 963 or 964) return DeviceType.Controller;
            if (major is 5 or 1280 && minor is int m) {
                if ((m & 0x10) != 0) return DeviceType.Keyboard;
                if ((m & 0x20) != 0) return DeviceType.Mouse;
                if ((m & 0xF) is 1 or 2) return DeviceType.Controller;
            }
            if (major is 4 or 1024 && minor is 1 or 2 or 6) return DeviceType.Headphones;
        }
        string s = name.ToLowerInvariant();
        if (Regex.IsMatch(s, "headphone|headset|earbud|airpods|wh-1000|wf-1000|galaxy buds|soundcore|freebuds|наушники|гарнитура|jabra|^jbl tune")) return DeviceType.Headphones;
        if (Regex.IsMatch(s, @"keyboard|клавиатур|keychron|mx keys|magic keyboard|\bk380\b")) return DeviceType.Keyboard;
        if (Regex.IsMatch(s, "mouse|мышь|mx master|mx anywhere|trackball|magic mouse")) return DeviceType.Mouse;
        if (Regex.IsMatch(s, "controller|gamepad|геймпад|контроллер|dualsense|dualshock|xbox|joy-con|8bitdo")) return DeviceType.Controller;
        return DeviceType.Other;
    }

    private static async Task<(int?, string?)> ReadBle(string id, bool readModel) {
        await RadioReads.WaitAsync();
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ct = timeout.Token;
            using var device = await BluetoothLEDevice.FromIdAsync(id).AsTask(ct);
            if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return (null, null);
            async Task<byte[]?> ReadCharacteristic(GattDeviceServicesResult services, Guid characteristic, BluetoothCacheMode mode) {
                try {
                    if (services.Status == GattCommunicationStatus.Success)
                        foreach (var service in services.Services) {
                            var chars = await service.GetCharacteristicsForUuidAsync(characteristic, BluetoothCacheMode.Cached).AsTask(ct);
                            if (chars.Status != GattCommunicationStatus.Success) continue;
                            foreach (var c in chars.Characteristics) {
                                var result = await c.ReadValueAsync(mode).AsTask(ct);
                                if (result.Status != GattCommunicationStatus.Success || result.Value.Length is 0 or > 256) continue;
                                using var reader = DataReader.FromBuffer(result.Value);
                                byte[] data = new byte[(int)result.Value.Length];
                                reader.ReadBytes(data);
                                return data;
                            }
                        }
                    return null;
                } finally { foreach (var service in services.Services) service.Dispose(); }
            }
            int? battery = null;
            string? model = null;
            try {
                var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(ct);
                var data = await ReadCharacteristic(services, GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
                if (data is not [<= 100] && !ct.IsCancellationRequested) {
                    services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(ct);
                    data = await ReadCharacteristic(services, GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached);
                }
                if (data is [<= 100]) battery = data[0];
            } catch (Exception) { /* Continue with data published by Windows. */ }
            if (readModel && !ct.IsCancellationRequested) try {
                var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.DeviceInformation, BluetoothCacheMode.Cached).AsTask(ct);
                var data = await ReadCharacteristic(services, GattCharacteristicUuids.ModelNumberString, BluetoothCacheMode.Cached);
                if (data is not null) model = new string(Encoding.UTF8.GetString(data).Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (model?.Length is 0 or > 80) model = null;
            } catch (Exception) { }
            return (battery, model);
        } catch (Exception) { return (null, null); }
        finally { RadioReads.Release(); }
    }

    private Accessory[] MergeControllers(List<Accessory> devices) {
        var native = new List<GameReading>();
        var debug = new List<string>();
        try {
            foreach (var pad in Gamepad.Gamepads) {
                var raw = RawGameController.FromGameController(pad);
                if (raw is null) continue;
                var name = string.IsNullOrWhiteSpace(raw.DisplayName) ? "Геймпад" : raw.DisplayName;
                var report = pad.TryGetBatteryReport();
                var percent = ControllerMerge.CapacityPercent(
                    report.Status is BatteryStatus.Charging or BatteryStatus.Discharging or BatteryStatus.Idle,
                    report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours);
                string key = "game:" + (string.IsNullOrWhiteSpace(raw.NonRoamableId) ? native.Count.ToString() : raw.NonRoamableId);
                var accessory = new Accessory(key, name, DeviceType.Controller, "Windows Gaming Input", percent,
                    Charging: report.Status == BatteryStatus.Charging);
                native.Add(new(accessory, raw.HardwareVendorId, raw.HardwareProductId));
                debug.Add($"WGI | {name} | {raw.HardwareVendorId:X4}:{raw.HardwareProductId:X4} | {report.Status} | {accessory.Reading}");
            }
        } catch (Exception e) { Errors.Add($"Windows Gaming Input: {e.GetType().Name}"); }
        var xinput = XInput.Read();
        debug.AddRange(xinput.Select(d => $"XInput | {d.Name} | {d.Reading}"));
        HardwareDiagnostics = HardwareDiagnostics.Concat(debug).ToArray();
        return ControllerMerge.Merge(devices, native, xinput);
    }

}

static class XInput {
    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetBatteryInformation(uint index, byte type, out Battery data);
    [StructLayout(LayoutKind.Sequential)]
    private struct Battery { public byte Type, Level; }

    public static Accessory[] Read() {
        var devices = new List<Accessory>();
        try {
            for (uint i = 0; i < 4; i++) {
                if (XInputGetBatteryInformation(i, 0, out var b) == 0 && b.Type != 0 && b.Type != 1)
                    devices.Add(new($"xinput:{i}", $"Геймпад {i + 1}", DeviceType.Controller, "XInput",
                        Level: b.Type is 2 or 3 && b.Level <= 3 ? (Band)b.Level : null));
            }
        } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        return devices.ToArray();
    }
}
