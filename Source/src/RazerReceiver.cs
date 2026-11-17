using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ChargeTray;

internal sealed class RazerReceiver(RazerModel model) {
    internal RazerModel Model => model;
    private Task<Reading>? _inFlight;
    private string[] _known = [];
    private string? _preferred;
    private DateTime _next;
    private Reading _last = new(null, null, "нет показаний");
    private sealed record Reading(string? Path, int? Percent, string Note);

    internal async Task<(bool Receiver, int? Percent, string Details)> Read(string[] allPaths, bool force) {
        var paths = allPaths.Where(model.Matches).ToArray();
        bool present = paths.Length > 0;
        if (!_known.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) {
            _known = paths;
            _next = DateTime.MinValue;
            _last = new(null, null, "нет показаний");
            _preferred = null;
        }
        if (!present) return (false, null, "ресивер отключён");
        var now = DateTime.UtcNow;
        if (_inFlight is null && (force || now >= _next)) {
            var preferred = _preferred;
            _inFlight = Task.Run(() => ReadOnce(paths, preferred, model.Transaction));
        }
        if (_inFlight is not null) {
            try {
                // Windows HID calls can block in a driver. Do not start more
                // workers if one is still pending.
                var reading = await _inFlight.WaitAsync(TimeSpan.FromSeconds(2));
                _inFlight = null;
                if (reading.Path is null || paths.Contains(reading.Path, StringComparer.OrdinalIgnoreCase)) {
                    _last = reading;
                    if (reading.Percent.HasValue) _preferred = reading.Path;
                    _next = DateTime.UtcNow.AddSeconds(reading.Percent.HasValue ? 55 : 32);
                }
            } catch (TimeoutException) {
                return (true, null, "ожидание ответа драйвера");
            } catch (Exception ex) {
                _inFlight = null;
                _next = DateTime.UtcNow.AddSeconds(32);
                _last = new(null, null, ex.GetType().Name);
            }
        }
        return (true, _last.Percent, _last.Note);
    }

    private static Reading ReadOnce(string[] paths, string? preferred, byte transaction) {
        var trace = new List<string>();
        foreach (var path in paths.OrderBy(p => p.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 0 :
                     p.Contains("&mi_01", StringComparison.OrdinalIgnoreCase) ? 1 : 2).Take(12)) {
            using var metadata = Open(path, 0);
            if (GetCaps(metadata) is not Caps caps) {
                trace.Add("caps недоступны");
                continue;
            }
            string info = $"usage={caps.Page:X4}:{caps.Usage:X4} F={caps.Feature}";
            if (caps.Feature > 512 || (caps.Feature < RazerBatteryPacket.Length && caps.Page < 0xff00)) {
                trace.Add(info);
                continue;
            }

            int length = Math.Max((int)caps.Feature, RazerBatteryPacket.Length);
            byte[] request = new byte[length];
            RazerBatteryPacket.Query(transaction).CopyTo(request, 0);
            using var rw = Open(path, 0xc0000000);
            bool sentRw = !rw.IsInvalid && HidD_SetFeature(rw, request, (uint)request.Length);
            if (!sentRw && !HidD_SetFeature(metadata, request, (uint)request.Length)) {
                trace.Add($"{info} SetFeature={Marshal.GetLastPInvokeError()}");
                continue;
            }
            var handle = sentRw ? rw : metadata;
            for (int attempt = 0; attempt < 5; attempt++) {
                Thread.Sleep(attempt == 0 ? 55 : 65);
                byte[] reply = new byte[length];
                if (!HidD_GetFeature(handle, reply, (uint)reply.Length)) {
                    trace.Add($"{info} GetFeature={Marshal.GetLastPInvokeError()}");
                    break;
                }
                if (RazerBatteryPacket.Percent(reply.AsSpan(0, RazerBatteryPacket.Length)) is int level)
                    return new(path, level, $"{level}% · ответ ресивера");
                if (reply[7] == 0x07 && reply[8] == 0x80 && reply[1] is not (0 or 1)) {
                    trace.Add($"{info} статус={reply[1]:X2}");
                    break;
                }
                if (attempt == 4) trace.Add($"{info} нет ответа на 07:80");
            }
        }
        return new(null, null, trace.Count > 0 ? $"{paths.Length} HID: " + string.Join("; ", trace) : "нет ответа на запрос заряда");
    }

    internal readonly record struct Caps(ushort Usage, ushort Page, ushort Input, ushort Output, ushort Feature);
    internal static SafeFileHandle Open(string path, uint access, bool overlapped = false) =>
        CreateFileW(path, access, 3, IntPtr.Zero, 3, overlapped ? 0x40000000u : 0u, IntPtr.Zero);
    internal static Caps? GetCaps(SafeFileHandle handle) {
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var data)) return null;
        try {
            byte[] raw = new byte[64];
            if (HidP_GetCaps(data, raw) != 0x00110000) return null;
            return new Caps(BitConverter.ToUInt16(raw,0), BitConverter.ToUInt16(raw,2),
                BitConverter.ToUInt16(raw,4), BitConverter.ToUInt16(raw,6), BitConverter.ToUInt16(raw,8));
        } finally { HidD_FreePreparsedData(data); }
    }
    internal static string? Product(SafeFileHandle handle) {
        byte[] raw = new byte[256];
        return !handle.IsInvalid && HidD_GetProductString(handle, raw, (uint)raw.Length) ?
            Encoding.Unicode.GetString(raw).TrimEnd('\0') : null;
    }

    internal static string[] PresentInterfaces(Func<string,bool> match) {
        HidD_GetHidGuid(out Guid hid);
        for (int i = 0; i < 2; i++) {
            if (CM_Get_Device_Interface_List_SizeW(out uint size, ref hid, null, 0) != 0 || size < 2 || size > 262144)
                return [];
            byte[] list = new byte[checked((int)size * 2)];
            uint cr = CM_Get_Device_Interface_ListW(ref hid, null, list, size, 0);
            if (cr == 0)
                return Encoding.Unicode.GetString(list).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Where(match).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return [];
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid cls, string? instance, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid cls, string? instance, [Out] byte[] list, uint size, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", ExactSpelling = true)] private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll", ExactSpelling = true)] private static extern uint HidP_GetCaps(IntPtr data, [Out] byte[] capabilities);
    [DllImport("hid.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] buffer, uint length);
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, uint length);
    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, uint length);
}
