using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ChargeTray;

// Windows exposes some Bluetooth battery readings as a PnP property on the
// matching device node. It is used only after WinRT confirms the link exists.
static class PnpBattery {
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey {
        public Guid Format;
        public uint Id;
    }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info,
        StringBuilder deviceId, uint length, out uint needed);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceInfo info, ref PropertyKey key,
        out uint type, [Out] byte[] value, uint size, out uint needed, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    public static Dictionary<string, int> Read() {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var key = new PropertyKey { Format = Guid.Parse("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), Id = 2 };
        var id = new StringBuilder(512);
        byte[] bytes = new byte[8];
        foreach (string enumerator in new[] { "BTHENUM", "BTHLE" }) {
            var set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, 0x6);
            if (set == new IntPtr(-1)) continue;
            try {
                for (uint i = 0; ; i++) {
                    var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                    if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;
                    if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, bytes, 8, out uint count, 0)) continue;
                    int value = (type, count) switch {
                        (3, 1) => bytes[0], (5, 2) => BitConverter.ToUInt16(bytes), (7, 4) => BitConverter.ToInt32(bytes), _ => -1
                    };
                    if (value is < 0 or > 100) continue;
                    id.Clear();
                    if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, 512, out _)) continue;
                    var match = Regex.Match(id.ToString(), @"\\DEV_([0-9A-F]{12})(?:\\|$)", RegexOptions.IgnoreCase);
                    if (!match.Success)
                        match = Regex.Match(id.ToString(), @"&([0-9A-F]{12})_[0-9A-F]+(?:&|$)", RegexOptions.IgnoreCase);
                    if (match.Success) found.TryAdd(match.Groups[1].Value, value);
                }
            } finally { SetupDiDestroyDeviceInfoList(set); }
        }
        return found;
    }
}
