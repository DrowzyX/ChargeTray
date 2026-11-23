using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ChargeTray;

// Use only the OS inventory and read-only PnP properties. Never send a HID
// output/feature report to a device or try to interpret an unknown protocol.
static class HidInput {
    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice { public IntPtr Handle; public uint Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Key(Guid format, uint index) { public Guid Format = format; public uint Index = index; }
    private static readonly Key Battery = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly Key BusName = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);
    private static readonly Key FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly Regex HardwareId = new(@"VID_([0-9A-F]{4}).*?PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetRawInputDeviceList([Out] RawDevice[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, [Out] StringBuilder? data, ref uint size);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, uint limit, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, in Key key, out uint type, [Out] byte[] data, ref uint length, uint flags);

    internal sealed record Input(string Instance, DeviceType Type, string Name, string Transport, string Hardware, int? Percent) {
        public string Diagnostic => $"{(Type == DeviceType.Mouse ? "Мышь" : "Клавиатура")} | {Name} | {Transport} | {Hardware} | {(Percent is int p ? $"{p}%" : "драйвер не отдал заряд")}";
    }

    internal static Input[] Read() {
        uint count = 0, listSize = (uint)Marshal.SizeOf<RawDevice>();
        if (GetRawInputDeviceList(null, ref count, listSize) == uint.MaxValue || count is 0 or > 1024) return [];
        var list = new RawDevice[count];
        uint read = GetRawInputDeviceList(list, ref count, listSize);
        if (read == uint.MaxValue || read > list.Length) return [];
        var results = new Dictionary<string, Input>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list.Take((int)read)) {
            if (entry.Type is not (0 or 1)) continue;
            const uint NameCommand = 0x20000007;
            uint len = 0;
            if (GetRawInputDeviceInfoW(entry.Handle, NameCommand, null, ref len) == uint.MaxValue || len is 0 or > 2048) continue;
            var path = new StringBuilder((int)len + 1);
            if (GetRawInputDeviceInfoW(entry.Handle, NameCommand, path, ref len) == uint.MaxValue) continue;
            string link = path.ToString();
            int end = link.LastIndexOf("#{", StringComparison.Ordinal);
            if (!link.StartsWith(@"\\?\", StringComparison.Ordinal) || end <= 4) continue;
            string instance = link[4..end].Replace('#', '\\');
            if (instance.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) continue;
            DeviceType kind = entry.Type == 0 ? DeviceType.Mouse : DeviceType.Keyboard;
            string fallbackName = kind == DeviceType.Mouse ? "HID-мышь" : "HID-клавиатура";
            string name = fallbackName;
            string transport = "HID";
            string? batteryNode = null;
            int? percent = null;
            var id = new StringBuilder(512);
            if (CM_Locate_DevNodeW(out uint node, instance, 0) == 0) {
                for (int depth = 0; depth < 9; depth++) {
                    id.Clear();
                    if (CM_Get_Device_IDW(node, id, 512, 0) != 0) break;
                    string nodeId = id.ToString();
                    if (nodeId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) {
                        transport = "Bluetooth HID"; break;
                    }
                    bool usb = nodeId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase);
                    if (usb) transport = "USB / ресивер";
                    if (usb || nodeId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) {
                        var property = ReadString(node, BusName) ?? ReadString(node, FriendlyName);
                        if (name == fallbackName && !string.IsNullOrWhiteSpace(property)) name = property;
                        if (percent is null && ReadLevel(node) is int p) { percent = p; batteryNode = nodeId; }
                    }
                    if (CM_Get_Parent(out uint parent, node, 0) != 0) break;
                    node = parent;
                }
            }
            var hw = HardwareId.Match(instance);
            var hardware = hw.Success ? $"{hw.Groups[1].Value.ToUpperInvariant()}:{hw.Groups[2].Value.ToUpperInvariant()}" : "без VID:PID";
            string key = (batteryNode ?? instance) + ":" + kind;
            results.TryAdd(key, new Input(key, kind, name, transport, hardware, percent));
        }
        return results.Values.ToArray();
    }

    private static string? ReadString(uint node, Key key) {
        byte[] data = new byte[512];
        uint len = (uint)data.Length;
        if (CM_Get_DevNode_PropertyW(node, in key, out uint type, data, ref len, 0) != 0 || type != 0x12 || len is < 2 or > 512) return null;
        return Encoding.Unicode.GetString(data, 0, (int)len).TrimEnd('\0').Trim();
    }

    private static int? ReadLevel(uint node) {
        byte[] data = new byte[4];
        uint len = (uint)data.Length;
        if (CM_Get_DevNode_PropertyW(node, in Battery, out uint type, data, ref len, 0) != 0) return null;
        int value = (type, len) switch { (3, 1) => data[0], (5, 2) => BitConverter.ToUInt16(data), (7, 4) => BitConverter.ToInt32(data), _ => -1 };
        return value is >= 0 and <= 100 ? value : null;
    }
}
