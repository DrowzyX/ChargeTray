using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChargeTray;

// Exact receiver family: FF02 Feature=17 -> FF01 Input=17.
// Uses only documented GetPower (08 04) and never sends profile/LED/DPI commands.
// Hardware verification on M808-KS is still required; do not make up percentages.
internal sealed class RedragonPowerReader {
    private Task<Reading>? _inFlight;
    private string[] _paths = [];
    private DateTime _next;
    private Reading _last = new(null, false, "ожидание ответа");
    private sealed record Reading(int? Percent, bool Charging, string Detail);
    internal async Task<(bool Found, int? Percent, bool Charging, string Detail)> Read(bool force) {
        string[] paths = RazerReceiver.PresentInterfaces(p =>
            p.StartsWith(@"\\?\hid#vid_25a7&pid_fa07", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(@"\\?\hid#vid_25a7&pid_fa08", StringComparison.OrdinalIgnoreCase));
        if (!_paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) {
            _paths = paths;
            _last = new(null, false, "ожидание отчёта от ресивера");
            _next = DateTime.MinValue;
        }
        if (paths.Length == 0) return (false, null, false, "не подключён");
        if (_inFlight is null && (force || DateTime.UtcNow >= _next)) {
            _inFlight = Task.Run(() => ReadOnce(paths));
        }
        if (_inFlight is not null) {
            try {
                var response = await _inFlight.WaitAsync(TimeSpan.FromSeconds(2));
                _inFlight = null;
                _last = response;
                _next = DateTime.UtcNow.AddSeconds(response.Percent.HasValue ? 45 : 25);
            } catch (TimeoutException) {
                return (true, null, false, "драйвер не отвечает; повторные запросы не запускаются");
            } catch (Exception ex) {
                _inFlight = null;
                _last = new(null, false, "ошибка протокола: " + ex.GetType().Name);
                _next = DateTime.UtcNow.AddSeconds(25);
            }
        }
        return (true, _last.Percent, _last.Charging, _last.Detail);
    }

    private static async Task<Reading> ReadOnce(string[] paths) {
        string? featurePath = null, inputPath = null;
        foreach (var path in paths) {
            // Both reports must belong to interface MI_01 of the same receiver.
            if (!path.Contains("&mi_01", StringComparison.OrdinalIgnoreCase)) continue;
            using var metadata = RazerReceiver.Open(path, 0);
            if (RazerReceiver.GetCaps(metadata) is not { } caps) continue;
            if (caps.Page == 0xFF02 && caps.Usage == 0x0002 && caps.Feature == 17)
                featurePath = path;
            if (caps.Page == 0xFF01 && caps.Input == 17)
                inputPath = path;
        }
        if (featurePath is null || inputPath is null)
            return new(null, false, "нет пары FF02 FEATURE=17 и FF01 INPUT=17 на MI_01");
        try {
            using var inputHandle = RazerReceiver.Open(inputPath, 0x80000000, overlapped: true);
            if (inputHandle.IsInvalid)
                return new(null, false, "нет доступа на чтение FF01");
            using var stream = new FileStream(inputHandle, FileAccess.Read, 17, isAsync: true);
            using var featureHandle = RazerReceiver.Open(featurePath, 0xC0000000);
            if (featureHandle.IsInvalid)
                return new(null, false, "нет доступа к FF02 Feature");
            var request = RedragonPowerPacket.Query();
            if (!HidD_SetFeature(featureHandle, request, (uint)request.Length))
                return new(null, false, "GetPower не принят драйвером (" + Marshal.GetLastPInvokeError() + ")");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1100));
            byte[] data = new byte[17];
            while (!timeout.IsCancellationRequested) {
                int count = 0;
                while (count < data.Length) {
                    int got = await stream.ReadAsync(data.AsMemory(count), timeout.Token).ConfigureAwait(false);
                    if (got <= 0) return new(null, false, "HID Input поток закрыт");
                    count += got;
                }
                if (RedragonPowerPacket.Decode(data) is { } power)
                    return new(power.Percent, power.Charging,
                        power.Percent + "% · GetPower (FF02/FF01, шаг 10%)");
            }
            return new(null, false, "нет подтверждённого ответа GetPower");
        } catch (OperationCanceledException) {
            return new(null, false, "таймаут GetPower; мышь может спать");
        } catch (IOException) {
            return new(null, false, "ошибка HID I/O; закройте OEM-программу и повторите");
        } catch (UnauthorizedAccessException) {
            return new(null, false, "HID занят фирменным приложением");
        }
    }

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] data, uint length);
}
