using System.Runtime.InteropServices;

namespace ChargeTray;

internal static class WindowsBattery {
    [StructLayout(LayoutKind.Sequential)]
    private struct Status {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out Status status);

    internal static Accessory? Read() =>
        GetSystemPowerStatus(out var status) ? Decode(status.BatteryFlag, status.BatteryLifePercent) : null;

    internal static Accessory? Decode(byte flags, byte percent) {
        if (flags is 128 or 255 || (flags & 128) != 0 || percent > 100) return null;
        return new Accessory("windows:system-battery", "Батарея компьютера", DeviceType.SystemBattery,
            "Питание Windows", percent, Charging: (flags & 8) != 0);
    }
}
