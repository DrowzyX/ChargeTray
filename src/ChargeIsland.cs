using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ChargeTray;

// A small, non-activating overlay. It redraws on updates and transitions,
// not continuously. The notification icon remains owned by Windows.
sealed class ChargeIsland : Form {
    private const float RestW = 82, RestH = 24, OpenH = 44;
    private readonly System.Windows.Forms.Timer _visibility = new() { Interval = 950 };
    private readonly System.Threading.Timer _motion;
    private int _postingFrame;
    private volatile bool _running;
    private bool _resolutionRaised;
    private readonly Stopwatch _clock = new();
    private Accessory[] _readings = [];
    private int _deviceCount;
    private bool _enabled, _inside, _low;
    private float _progress, _from, _target, _scale = 1, _openWidth = 180;
    internal event Action<Point>? OpenPanel;

    public ChargeIsland() {
        Text = "ChargeTray — островок заряда";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(22, 21, 24);
        _visibility.Tick += (_, _) => Place();
        _motion = new System.Threading.Timer(_ => PostFrame(), null, Timeout.Infinite, Timeout.Infinite);
        DpiChanged += (_, _) => { if (Visible) BeginInvoke((Action)Place); };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    internal void UpdateIsland(bool enabled, Accessory[] devices) {
        _enabled = enabled;
        _deviceCount = devices.Length;
        _low = devices.Any(d => d.Low);
        // Keep display order stable. Put at-risk devices first when a large
        // inventory must be shortened to fit the quick preview.
        _readings = devices.Where(d => d.HasCurrentReading)
            .OrderByDescending(d => d.Low).ToArray();
        _openWidth = _readings.Length == 0 ? 180 : 30 + Math.Min(4, _readings.Length) * 70 + (_readings.Length > 4 ? 27 : 0);
        _visibility.Enabled = enabled && _deviceCount > 0;
        Place();
        if (Visible) Invalidate();
    }

    private void Place() => Place(checkFullscreen: true);

    private void Place(bool checkFullscreen) {
        if (IsDisposed) return;
        var screen = Screen.PrimaryScreen;
        if (!_enabled || _deviceCount == 0 || screen is null ||
            checkFullscreen && ChargeStrip.CoversScreen(screen.Bounds, IsHandleCreated ? Handle : IntPtr.Zero)) {
            _inside = false;
            StopMotion();
            _progress = _target = 0;
            if (Visible) Hide();
            return;
        }
        var area = screen.WorkingArea;
        _scale = Math.Clamp(DeviceDpi / 96f, .75f, 4);
        float fit = Math.Min(_openWidth, Math.Max(RestW, area.Width / _scale - 20));
        int w = (int)Math.Ceiling((RestW + (fit - RestW) * _progress) * _scale);
        int h = (int)Math.Ceiling((RestH + (OpenH - RestH) * _progress) * _scale);
        var next = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (int)(9 * _scale), w, h);
        if (next != Bounds || Region is null) {
            Bounds = next;
            using var outline = Shapes.Round(new RectangleF(0, 0, w, h), h / 2f - .4f);
            var old = Region;
            Region = new Region(outline);
            old?.Dispose();
        }
        if (!Visible) Show();
    }

    protected override void OnMouseEnter(EventArgs e) {
        base.OnMouseEnter(e);
        if (!_inside) { _inside = true; Transition(1); }
    }

    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e);
        _inside = false;
        Transition(0);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        OpenPanel?.Invoke(new Point(Left + Width / 2, Top + Height / 2));
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    private void PostFrame() {
        if (!_running || Interlocked.Exchange(ref _postingFrame, 1) != 0) return;
        try {
            BeginInvoke((Action)(() => {
                Interlocked.Exchange(ref _postingFrame, 0);
                if (!_running || IsDisposed) return;
                float t = Math.Min(1, (float)(_clock.Elapsed.TotalMilliseconds / 220));
                float eased = t * t * (3 - 2 * t);
                _progress = _from + (_target - _from) * eased;
                if (t >= 1) StopMotion();
                Place(checkFullscreen: false);
                if (Visible) Invalidate();
            }));
        } catch (InvalidOperationException) { Interlocked.Exchange(ref _postingFrame, 0); }
    }

    private void StopMotion() {
        if (!_running) return;
        _running = false;
        _motion.Change(Timeout.Infinite, Timeout.Infinite);
        if (_resolutionRaised) { timeEndPeriod(1); _resolutionRaised = false; }
    }

    private void Transition(float target) {
        if (target == _target) return;
        _target = target;
        if (!SystemInformation.IsMenuAnimationEnabled) {
            StopMotion();
            _progress = target;
            Place();
            Invalidate();
            return;
        }
        _from = _progress;
        _clock.Restart();
        if (!_running) {
            _resolutionRaised = timeBeginPeriod(1) == 0;
            _running = true;
            // Windows/DWM may coalesce repaints. One pending frame is enough.
            _motion.Change(0, 8);
        }
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g = e.Graphics;
        g.ScaleTransform(_scale, _scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float w = Width / _scale, h = Height / _scale, center = w / 2f;
        var shadow = Color.FromArgb(25, 24, 28);
        var champagne = AccentTheme.Accent;
        var muted = Color.FromArgb(169, 156, 151);
        var coral = Color.FromArgb(248, 153, 159);
        using (var fill = new LinearGradientBrush(new RectangleF(0, 0, w, h), Color.FromArgb(35, 32, 35), shadow, 90))
            g.FillRectangle(fill, 0, 0, w, h);
        using var contour = Shapes.Round(new RectangleF(.55f, .55f, w - 1.1f, h - 1.1f), (h - 1.1f) / 2f - .35f);
        using var border = new Pen(Color.FromArgb(115, AccentTheme.Accent));
        g.DrawPath(border, contour);

        float compactAlpha = 1 - Math.Clamp((_progress - .12f) / .53f, 0, 1);
        float expandedAlpha = Math.Clamp((_progress - .49f) / .48f, 0, 1);
        Color Fade(Color c, float amount) => Color.FromArgb((int)Math.Round(amount * 255), c);
        if (compactAlpha > 0) {
            var fg = Fade(_low ? coral : champagne, compactAlpha);
            TrayArt.Battery(g, new RectangleF(center - 18, (h - 17) / 2, 17, 17), fg, _readings.Length > 0);
            using var dot = new SolidBrush(Fade(_low ? coral : muted, compactAlpha));
            for (int i = 0; i < Math.Clamp(_deviceCount, 1, 3); i++)
                g.FillEllipse(dot, center + 3 + i * 6, (h - 3) / 2, 3, 3);
        }
        if (expandedAlpha <= 0) return;
        var clip = g.Save();
        g.SetClip(new RectangleF(7, 0, Math.Max(1, w - 14), h), CombineMode.Intersect);
        using var font = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center };
        if (_readings.Length == 0) {
            using var label = new SolidBrush(Fade(muted, expandedAlpha));
            using var centered = new StringFormat(format) { Alignment = StringAlignment.Center };
            g.DrawString("Нет показаний", font, label, new RectangleF(0, 0, w, h), centered);
            g.Restore(clip);
            return;
        }
        float targetW = Math.Min(_openWidth, Math.Max(RestW, Screen.PrimaryScreen!.WorkingArea.Width / _scale - 20));
        int visible = Math.Clamp((int)((targetW - 30) / 70), 1, Math.Min(4, _readings.Length));
        int rest = _readings.Length - visible;
        if (rest > 0 && visible > 1 && 30 + visible * 70 + 26 > targetW) { visible--; rest++; }
        float total = 70 * visible + (rest > 0 ? 26 : 0);
        float x = (w - total) / 2;
        using var divider = new Pen(Fade(Color.FromArgb(70, 59, 59), expandedAlpha));
        for (int i = 0; i < visible; i++, x += 70) {
            var device = _readings[i];
            var color = device.Low ? coral : Color.FromArgb(251, 247, 242);
            Shapes.Glyph(g, device.Type, new RectangleF(x + 5, (h - 17) / 2, 17, 17), Fade(device.Low ? coral : champagne, expandedAlpha));
            using var ink = new SolidBrush(Fade(color, expandedAlpha));
            g.DrawString(device.Percent is int p ? $"{p}%" : device.Low ? "Мало" : "Уров.", font, ink,
                new RectangleF(x + 25, 0, 45, h), format);
            if (i + 1 < visible || rest > 0) g.DrawLine(divider, x + 68, h / 2 - 8, x + 68, h / 2 + 8);
        }
        if (rest > 0) {
            using var overflow = new SolidBrush(Fade(muted, expandedAlpha));
            g.DrawString($"+{rest}", font, overflow, new RectangleF(x + 4, 0, 27, h), format);
        }
        g.Restore(clip);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) { StopMotion(); _motion.Dispose(); _visibility.Dispose(); }
        base.Dispose(disposing);
    }
}
