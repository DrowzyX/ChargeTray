using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.ComponentModel;

namespace ChargeTray;

internal static class FluentStyle {
    private static string _mode = "dark";
    internal static void SetTheme(string mode) =>
        _mode = mode is "dark" or "light" or "system" ? mode : "dark";
    internal static bool IsDark {
        get {
            if (_mode != "system") return _mode != "light";
            try {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return Convert.ToInt32(key?.GetValue("AppsUseLightTheme") ?? 0) == 0;
            } catch { return true; }
        }
    }
    internal static Color Background => IsDark ? Color.FromArgb(18, 21, 27) : Color.FromArgb(247, 248, 251);
    internal static Color Surface => IsDark ? Color.FromArgb(30, 34, 43) : Color.FromArgb(255, 255, 255);
    internal static Color SurfaceHover => IsDark ? Color.FromArgb(42, 47, 59) : Color.FromArgb(237, 240, 247);
    internal static Color Text => IsDark ? Color.FromArgb(244, 246, 251) : Color.FromArgb(30, 35, 47);
    internal static Color Muted => IsDark ? Color.FromArgb(158, 169, 188) : Color.FromArgb(96, 104, 118);
    internal static Color Border => IsDark ? Color.FromArgb(69, 75, 89) : Color.FromArgb(217, 222, 232);
    internal static Font Font(float size = 10, bool bold = false) =>
        new("Segoe UI Variable", size, bold ? FontStyle.Bold : FontStyle.Regular);

    internal static GraphicsPath Round(Rectangle r, int radius) {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) return path;
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
    internal static void DrawRounded(Graphics g, Rectangle bounds, Color fill, int radius = 12, bool border = true) {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = Round(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (border) {
            using var pen = new Pen(Border);
            g.DrawPath(pen, path);
        }
    }
}

internal sealed class FluentCard : Panel {
    internal FluentCard() {
        DoubleBuffered = true;
        BackColor = FluentStyle.Background;
        Padding = new Padding(17);
        Margin = new Padding(0, 0, 0, 13);
        ResizeRedraw = true;
    }
    protected override void OnPaintBackground(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        FluentStyle.DrawRounded(e.Graphics, new Rectangle(1, 1, Width - 3, Height - 3),
            FluentStyle.Surface, 13);
    }
}

internal sealed class FluentButton : Control {
    private bool _over, _down;
    internal bool Selected;
    internal bool Primary;
    internal string Symbol = "";
    internal FluentButton(string text = "") {
        Text = text;
        Font = FluentStyle.Font(10);
        ForeColor = FluentStyle.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Size = new Size(140, 38);
        Margin = new Padding(3, 3, 3, 3);
    }
    protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _over = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color accent = AccentTheme.Accent;
        Color bg = !Enabled ? Color.FromArgb(37, 40, 47) :
            Selected || Primary ? Color.FromArgb(49, 58, 74) :
            _down ? Color.FromArgb(58, 65, 80) :
            _over ? FluentStyle.SurfaceHover : FluentStyle.Surface;
        FluentStyle.DrawRounded(g, new Rectangle(1, 1, Width - 3, Height - 3), bg, 9);
        if (Selected) {
            using var marker = new SolidBrush(accent);
            g.FillRectangle(marker, 12, Height - 4, Math.Max(10, Width - 24), 3);
        }
        string display = string.IsNullOrEmpty(Symbol) ? Text : Symbol + "  " + Text;
        TextRenderer.DrawText(g, display, Font, new Rectangle(10, 1, Width - 20, Height - 2),
            Enabled ? FluentStyle.Text : FluentStyle.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class FluentToggle : Control {
    private bool _checked;
    private float _fraction;
    private readonly System.Windows.Forms.Timer _motion = new() { Interval = 16 };
    internal event Action<bool>? ValueChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Checked {
        get => _checked;
        set {
            if (_checked == value) return;
            _checked = value;
            if (SystemInformation.IsMenuAnimationEnabled) _motion.Start();
            else _fraction = value ? 1 : 0;
            ValueChanged?.Invoke(value);
            Invalidate();
        }
    }
    internal FluentToggle(string label, bool initial) {
        Text = label;
        Font = FluentStyle.Font(10);
        ForeColor = FluentStyle.Text;
        _checked = initial;
        _fraction = initial ? 1 : 0;
        Size = new Size(490, 43);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _motion.Tick += (_, _) => {
            var target = _checked ? 1f : 0f;
            _fraction += (target - _fraction) * .32f;
            if (Math.Abs(_fraction - target) < .013f) { _fraction = target; _motion.Stop(); }
            Invalidate();
        };
    }
    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(3, 0, Width - 74, Height),
            FluentStyle.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var track = new Rectangle(Width - 59, (Height - 25) / 2, 49, 25);
        using (var p = FluentStyle.Round(track, 13))
        using (var brush = new SolidBrush(_checked ? AccentTheme.Accent : Color.FromArgb(68, 74, 88))) g.FillPath(brush, p);
        var thumbX = track.X + 4 + (int)Math.Round(24 * _fraction);
        using var thumb = new SolidBrush(_checked ? FluentStyle.Background : FluentStyle.Text);
        g.FillEllipse(thumb, thumbX, track.Y + 4, 17, 17);
    }
    internal void ThemeFrame() { if (_checked) Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) _motion.Dispose(); base.Dispose(disposing); }
}

internal sealed class FluentHeader : Panel {
    internal FluentHeader() {
        Height = 69;
        BackColor = FluentStyle.Background;
        Dock = DockStyle.Top;
        Padding = new Padding(20, 13, 20, 10);
    }
}

internal static class UiElements {
    internal static Label Label(string value, float size = 10, bool bold = false, bool muted = false) =>
        new() { Text = value, Font = FluentStyle.Font(size, bold),
            ForeColor = muted ? FluentStyle.Muted : FluentStyle.Text, AutoSize = false,
            BackColor = Color.Transparent };
    internal static Panel Bar(int height = 6) => new() { Height = height, BackColor = Color.FromArgb(61, 68, 81) };
}
