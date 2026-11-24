using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ChargeTray;

// A separate, non-activating tool window. Explorer still owns the taskbar
// and its notification area; no injection, shell hooks or taskbar overlays.
sealed class ChargeStrip : Form {
    private readonly System.Windows.Forms.Timer _visibility = new() { Interval = 900 };
    private readonly record struct Slot(Accessory Device, string Caption, float Width, float ValueWidth);
    private Slot[] _slots = [];
    private bool _enabled;
    private bool _light;
    private float _scale = 1;
    private float _logicalWidth;
    internal event Action? OpenPanel;

    public ChargeStrip() {
        Text = "ChargeTray — индикатор заряда";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Hand;
        _visibility.Tick += (_, _) => Reposition();
        DpiChanged += (_, _) => { if (Visible) BeginInvoke(Reposition); };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    internal void UpdateStrip(bool enabled, bool light, Accessory[] devices) {
        _enabled = enabled;
        _light = light;
        using var label = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var value = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
        Size Measure(string text, Font font) => TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
        var groups = devices.Where(d => d.HasCurrentReading).GroupBy(d => d.Type).ToArray();
        var items = new List<Slot>();
        foreach (var group in groups) {
            int i = 0;
            foreach (var d in group) {
                string caption = DeviceLabel(d.Type);
                if (group.Count() > 1) caption += $" {++i}";
                string shown = Value(d);
                int valueWidth = Measure(shown, value).Width + 1;
                var width = 31 + Measure(caption, label).Width + valueWidth;
                items.Add(new Slot(d, caption, width, valueWidth));
            }
        }
        _slots = items.ToArray();
        _visibility.Enabled = _enabled && _slots.Length > 0;
        Reposition();
        Invalidate();
    }

    private static string DeviceLabel(DeviceType device) => device switch {
        DeviceType.Mouse => "Мышь", DeviceType.Keyboard => "Клавиатура", DeviceType.Headphones => "Наушники",
        DeviceType.Controller => "Геймпад", DeviceType.SystemBattery => "Компьютер", _ => "Устройство"
    };
    private static string Value(Accessory d) => d.Percent is int p ? $"{p}%" : d.Level switch {
        Band.Empty or Band.Low => "Мало", Band.Medium => "Средний", Band.Full => "Высокий", _ => "—"
    };

    private void Reposition() {
        if (IsDisposed) return;
        if (!_enabled || _slots.Length == 0) { if (Visible) Hide(); return; }
        var primary = Screen.PrimaryScreen;
        if (primary is null || CoversScreen(primary.Bounds, IsHandleCreated ? Handle : IntPtr.Zero)) { if (Visible) Hide(); return; }

        // WorkingArea excludes the taskbar on all docking sides. Keep the
        // widget just INSIDE that area so it cannot steal shell input.
        var area = primary.WorkingArea;
        var screen = primary.Bounds;
        _scale = Math.Max(1, DeviceDpi / 96f);
        var available = Math.Max(100, (area.Width - 24) / _scale);
        _logicalWidth = Math.Min(available, 18 + _slots.Sum(s => s.Width + 8));
        var width = (int)Math.Ceiling(_logicalWidth * _scale);
        var height = (int)Math.Ceiling(34 * _scale);
        var margin = (int)(8 * _scale);
        int x = screen.Left < area.Left && screen.Right == area.Right ? area.Left + margin : area.Right - width - margin;
        int y = screen.Top < area.Top ? area.Top + margin : area.Bottom - height - margin;
        var bounds = new Rectangle(x, y, width, height);

        if (Bounds != bounds) {
            Bounds = bounds;
            using var shape = Shapes.Round(new RectangleF(0, 0, width, height), 10 * _scale);
            var old = Region;
            Region = new Region(shape);
            old?.Dispose();
        }
        if (!Visible) Show();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) OpenPanel?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g = e.Graphics;
        g.ScaleTransform(_scale, _scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Color fg = _light ? Color.FromArgb(33, 28, 28) : Color.FromArgb(248, 245, 241);
        Color muted = _light ? Color.FromArgb(90, 78, 75) : Color.FromArgb(201, 193, 190);
        var bg = _light ? Color.FromArgb(250, 247, 243) : Color.FromArgb(28, 26, 29);
        g.Clear(bg);
        using var frame = Shapes.Round(new RectangleF(.5f, .5f, _logicalWidth - 1, 33), 10);
        using var hairline = new Pen(Color.FromArgb(_light ? 145 : 100, AccentTheme.Accent));
        g.DrawPath(hairline, frame);
        using var nameFont = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var numberFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
        using var mutedBrush = new SolidBrush(muted);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        float x = 11;
        for (int i = 0; i < _slots.Length; i++) {
            var s = _slots[i];
            if (x + s.Width > _logicalWidth - 5) {
                g.DrawString("…", numberFont, mutedBrush, new RectangleF(_logicalWidth - 16, 0, 15, 34), fmt);
                break;
            }
            if (i > 0) g.DrawLine(hairline, x - 5, 10, x - 5, 24);
            Shapes.Glyph(g, s.Device.Type, new RectangleF(x, 8, 18, 18), fg, 1.9f);
            g.DrawString(s.Caption, nameFont, mutedBrush, new RectangleF(x + 22, 0, s.Width - 25 - s.ValueWidth, 34), fmt);
            var tint = !s.Device.Low ? AccentTheme.Accent : _light ? Color.FromArgb(166, 30, 47) : Color.FromArgb(255, 167, 171);
            using var valueBrush = new SolidBrush(tint);
            g.DrawString(Value(s.Device), numberFont, valueBrush, new RectangleF(x + s.Width - s.ValueWidth - 2, 0, s.ValueWidth + 4, 34), fmt);
            x += s.Width + 8;
        }
    }

    protected override void Dispose(bool disposing) {
        if (disposing) _visibility.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rectangle);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool IsIconic(IntPtr hwnd);

    internal static bool CoversScreen(Rectangle screen, IntPtr ownWindow) {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || hwnd == ownWindow || IsIconic(hwnd) ||
            !GetWindowRect(hwnd, out var r)) return false;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }
}
