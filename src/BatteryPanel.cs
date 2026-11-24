using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ChargeTray;

sealed class BatteryPanel : Form {
    private const int W = 410, Header = 104, Card = 83, Gap = 9, Footer = 44, Pad = 18;
    private const int MaxRows = 4;
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 16 };
    private readonly Stopwatch _clock = new();
    private readonly ToolTip _hints = new() { AutoPopDelay = 9000, InitialDelay = 500 };
    private string? _hint;
    private Accessory[] _items = [];
    private bool _busy, _demo;
    private string[] _errors = [];
    private DateTime? _updated;
    private float _scale = 1, _progress = 1;
    private Point _anchor;
    private int _stripClearance;
    private bool _underIsland;
    private int _offset;
    private DateTime _hiddenAt = DateTime.MinValue;
    internal bool ClickedOutsideJustNow => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(230);
    internal event Action? RefreshRequested;

    public BatteryPanel() {
        Text = "ChargeTray — заряд устройств";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(18, 18, 21);
        _animation.Tick += (_, _) => {
            _progress = Math.Min(1f, (float)(_clock.Elapsed.TotalMilliseconds / 360));
            Invalidate();
            if (_progress >= 1) _animation.Stop();
        };
        Deactivate += (_, _) => { if (Visible) ClosePanel(); };
        KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Escape) ClosePanel();
            if (e.KeyCode == Keys.F5) RefreshRequested?.Invoke();
            if (e.KeyCode == Keys.Down) MoveCards(1);
            if (e.KeyCode == Keys.Up) MoveCards(-1);
        };
        DpiChanged += (_, _) => { if (Visible) Fit(); };
    }

    internal void OpenAt(Point anchor, int stripClearance = 0, bool underIsland = false) {
        _anchor = anchor;
        _stripClearance = stripClearance;
        _underIsland = underIsland;
        if (!Visible) {
            _offset = 0;
            Fit();
            Show();
            Fit();
            Activate();
            Animate();
        } else { Fit(); Activate(); }
    }

    private void ClosePanel() {
        _animation.Stop();
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    internal void UpdateData(Accessory[] items, bool busy, DateTime? updated, bool demo, string[] errors) {
        bool layout = items.Length != _items.Length;
        _items = items;
        _errors = errors;
        _busy = busy;
        _updated = updated;
        _demo = demo;
        _offset = Math.Min(_offset, Math.Max(0, items.Length - VisibleRows()));
        if (!Visible) return;
        if (layout) Fit();
        Invalidate();
    }

    private void Animate() {
        bool reduced;
        try { reduced = !SystemInformation.IsMenuAnimationEnabled; }
        catch { reduced = false; }
        _progress = reduced ? 1 : 0;
        if (!reduced) { _clock.Restart(); _animation.Start(); }
        Invalidate();
    }

    private int Rows() {
        var area = Screen.FromPoint(_anchor).WorkingArea;
        int max = Math.Clamp((int)((area.Height / _scale - Header - Footer - 24 - _stripClearance + Gap) / (Card + Gap)), 1, MaxRows);
        return Math.Max(1, Math.Min(max, _items.Length));
    }

    private int VisibleRows() => _items.Length == 0 ? 0 : Rows();

    private void Fit() {
        var area = Screen.FromPoint(_anchor).WorkingArea;
        _scale = Math.Min(Math.Max(1, DeviceDpi / 96f), (area.Width - 16) / (float)W);
        var size = new Size((int)Math.Ceiling(W * _scale), (int)Math.Ceiling((Header + Rows() * (Card + Gap) - Gap + Footer) * _scale));
        int margin = (int)(12 * _scale);
        int left = _underIsland ? _anchor.X - size.Width / 2 : _anchor.X - size.Width + margin;
        int x = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - size.Width - margin));
        int clearance = (int)(_stripClearance * _scale);
        bool nearTop = _underIsland || _anchor.Y < area.Top || _stripClearance > 0 && _anchor.Y < area.Top + 80 * _scale;
        int y = nearTop ? area.Top + margin + clearance : Math.Max(area.Top + margin, area.Bottom - size.Height - margin - clearance);
        if (Bounds == new Rectangle(new Point(x, y), size) && Region is not null) return;
        Bounds = new Rectangle(new Point(x, y), size);
        using var r = Shapes.Round(new RectangleF(0, 0, size.Width, size.Height), 22 * _scale);
        Region? old = Region;
        Region = new Region(r);
        old?.Dispose();
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        float x = e.X / _scale, y = e.Y / _scale;
        bool isAction = new RectangleF(319, 17, 74, 38).Contains(x, y);
        Cursor = isAction ? Cursors.Hand : Cursors.Default;
        string? hint = isAction ? x >= 361 ? "Закрыть (Esc)" : "Обновить (F5)" : null;
        if (x >= Pad && x < W - Pad && y >= Header) {
            int row = (int)((y - Header) / (Card + Gap));
            if (row < VisibleRows() && (y - Header) % (Card + Gap) < Card) {
                var d = _items[_offset + row];
                hint = d.Name + (d.Model is { Length: > 0 } && d.Model != d.Name ? "\nМодель: " + d.Model : "") +
                    "\n" + d.Via + " · " + d.Reading + (d.Detail is { Length: > 0 } ? "\n" + d.Detail : "");
            }
        }
        if (hint == _hint) return;
        _hint = hint;
        _hints.SetToolTip(this, hint);
    }

    protected override void OnMouseClick(MouseEventArgs e) {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        float x = e.X / _scale, y = e.Y / _scale;
        if (new RectangleF(361, 17, 32, 38).Contains(x, y)) ClosePanel();
        else if (new RectangleF(319, 17, 38, 38).Contains(x, y)) RefreshRequested?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e) {
        base.OnMouseWheel(e);
        MoveCards(e.Delta < 0 ? 1 : -1);
    }

    private void MoveCards(int step) {
        _offset = Math.Clamp(_offset + step, 0, Math.Max(0, _items.Length - VisibleRows()));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args) {
        base.OnPaint(args);
        var g = args.Graphics;
        g.ScaleTransform(_scale, _scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float h = Height / _scale;
        var background = new RectangleF(0, 0, W, h);
        var tone = AccentTheme.Accent;
        var tinted = Color.FromArgb((tone.R + 39 * 7) / 8, (tone.G + 37 * 7) / 8, (tone.B + 37 * 7) / 8);
        using var wash = new LinearGradientBrush(background, tinted, Color.FromArgb(18, 18, 21), 65f);
        g.FillRectangle(wash, background);
        using var border = new Pen(Color.FromArgb(95, AccentTheme.Accent));
        using var frame = Shapes.Round(new RectangleF(.5f, .5f, W - 1, h - 1), 22);
        g.DrawPath(border, frame);
        var champagne = AccentTheme.Accent;
        var muted = Color.FromArgb(180, 174, 173);
        var white = Color.FromArgb(250, 247, 244);
        Fill(g, new RectangleF(19, 20, 35, 35), 10, Color.FromArgb(35, AccentTheme.Accent));
        TrayArt.Battery(g, new RectangleF(24, 25, 25, 25), champagne);
        DrawText(g, "ChargeTray", 65, 19, 245, 16, white, FontStyle.Bold);
        DrawText(g, _demo ? "РЕЖИМ ДЕМО · ПРИМЕР ДАННЫХ" : "БАТАРЕИ УСТРОЙСТВ", 65, 39, 248, 10, muted);
        DrawText(g, "Заряд устройств", 19, 64, 370, 25, white, FontStyle.Bold);
        Fill(g, new RectangleF(320, 20, 35, 34), 10, Color.FromArgb(48, 45, 47));
        Fill(g, new RectangleF(361, 20, 30, 34), 10, Color.FromArgb(48, 45, 47));
        using var action = new Pen(muted, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(action, 331, 30, 13, 13, 40, 282);
        g.DrawLines(action, [new PointF(344, 29), new PointF(344, 34), new PointF(339, 33)]);
        g.DrawLine(action, 372, 33, 380, 41);
        g.DrawLine(action, 380, 33, 372, 41);

        if (_items.Length == 0) {
            Fill(g, new RectangleF(Pad, Header, W - 2 * Pad, Card), 15, Color.FromArgb(40, 38, 41));
            DrawText(g, _busy ? "Проверяем подключения…" : "Нет обнаруженных устройств", 32, Header + 18, 340, 15, white, FontStyle.Bold);
            DrawText(g, _errors.Length > 0 ? "Не все источники ответили. Нажмите обновить." :
                "Подключите Bluetooth-устройство или геймпад.", 32, Header + 44, 342, 12, muted);
        }

        for (int i = 0; i < VisibleRows(); i++) {
            var d = _items[_offset + i];
            float y = Header + i * (Card + Gap);
            var paper = Color.FromArgb(40, 38, 41);
            var accent = d.IsStale ? muted : d.Low ? Color.FromArgb(246, 153, 159) : d.Percent is < 30 ? Color.FromArgb(235, 183, 127) : champagne;
            var tile = new RectangleF(Pad, y, W - 2 * Pad, Card);
            Fill(g, tile, 14, paper);
            using (var contour = Shapes.Round(new RectangleF(Pad + .5f, y + .5f, W - 2 * Pad - 1, Card - 1), 14))
            using (var outline = new Pen(Color.FromArgb(60, 53, 54)))
                g.DrawPath(outline, contour);
            Fill(g, new RectangleF(29, y + 11, 39, 39), 11, Color.FromArgb(55, 50, 51));
            Shapes.Glyph(g, d.Type, new RectangleF(36, y + 18, 25, 25), d.Low ? accent : white);
            DrawText(g, d.Name, 79, y + 10, 209, 15, white, FontStyle.Bold);
            string model = !string.IsNullOrWhiteSpace(d.Model) && !d.Model.Equals(d.Name, StringComparison.OrdinalIgnoreCase)
                ? $"{d.Model} · {d.Via}" : $"{TypeLabel(d.Type)} · {d.Via}";
            if (d.Charging) model = "⚡ Заряжается · " + model;
            else if (d.UsbConnected) model = "USB подключён · зарядка не подтверждена";
            else if (d.Detail?.StartsWith("⚡? Вероятно", StringComparison.Ordinal) == true)
                model = "⚡? Вероятно заряжается · " + model;
            DrawText(g, model, 79, y + 34, 281, 11, muted);
            DrawText(g, d.Reading, 290, y + 10, 89, d.Percent.HasValue ? 23 : 12,
                d.IsStale ? muted : d.Low ? accent : d.HasCurrentReading ? white : muted, FontStyle.Bold, true);
            var track = new RectangleF(29, y + 60, 352, 6);
            Fill(g, track, 3, Color.FromArgb(65, 59, 62));
            // A coarse controller reading is deliberately not represented as a numerical bar.
            if (d.Percent is int p && p > 0) {
                float easing = 1 - MathF.Pow(1 - _progress, 3);
                float partial = track.Width * p / 100f * easing;
                if (partial > 0.5f) Fill(g, track with { Width = partial }, Math.Min(partial / 2, 3), accent);
            }
        }
        using (var divider = new Pen(Color.FromArgb(55, 48, 51)))
            g.DrawLine(divider, 20, h - Footer + 6, W - 20, h - Footer + 6);
        float footerY = h - Footer + 15;
        var status = _busy ? "Обновление…" : _errors.Length > 0 ? "Часть данных недоступна" : _updated.HasValue ? $"Обновлено в {_updated:HH:mm:ss}" : "Ожидание данных";
        DrawText(g, status, 20, footerY, 232, 11, muted);
        if (_items.Length > VisibleRows())
            DrawText(g, $"▲  {_offset + 1}–{_offset + VisibleRows()} из {_items.Length}  ▼", 260, footerY, 129, 11, muted, right: true);
        else DrawText(g, $"Устройств: {_items.Length}", 260, footerY, 129, 11, muted, right: true);
    }

    private static string TypeLabel(DeviceType t) => t switch {
        DeviceType.Keyboard => "Клавиатура", DeviceType.Mouse => "Мышь", DeviceType.Headphones => "Наушники",
        DeviceType.Controller => "Геймпад", DeviceType.SystemBattery => "Системный аккумулятор", _ => "Устройство"
    };

    private static void DrawText(Graphics g, string s, float x, float y, float w, float size, Color color,
        FontStyle style = FontStyle.Regular, bool right = false) {
        using var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat(StringFormatFlags.NoWrap) {
            Trimming = StringTrimming.EllipsisCharacter, Alignment = right ? StringAlignment.Far : StringAlignment.Near
        };
        g.DrawString(s, font, brush, new RectangleF(x, y, w, size + 8), fmt);
    }

    private static void Fill(Graphics g, RectangleF r, float radius, Color c) {
        using var path = Shapes.Round(r, radius);
        using var brush = new SolidBrush(c);
        g.FillPath(brush, path);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) { _animation.Dispose(); _hints.Dispose(); }
        base.Dispose(disposing);
    }
}

static class Shapes {
    internal static GraphicsPath Round(RectangleF r, float radius) {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static void Glyph(Graphics g, DeviceType type, RectangleF bounds, Color c, float thickness = 1.8f) {
        var state = g.Save();
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
        using var p = new Pen(c, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(c);
        void Rounded(float x, float y, float w, float h, float r) {
            using var path = Round(new RectangleF(x, y, w, h), r);
            g.DrawPath(p, path);
        }
        switch (type) {
            case DeviceType.Keyboard:
                Rounded(2, 5, 20, 14, 3);
                for (int i = 6; i <= 18; i += 4) for (int j = 9; j <= 12; j += 3) g.FillEllipse(brush, i - .8f, j - .7f, 1.6f, 1.6f);
                g.DrawLine(p, 8, 15, 16, 15);
                break;
            case DeviceType.Mouse:
                Rounded(6, 2, 12, 20, 6);
                g.DrawLine(p, 12, 6, 12, 9);
                break;
            case DeviceType.Headphones:
                g.DrawArc(p, 3, 3, 18, 17, 180, 180);
                g.DrawLine(p, 3, 11, 3, 15);
                g.DrawLine(p, 21, 11, 21, 15);
                Rounded(3, 13, 5, 8, 2);
                Rounded(16, 13, 5, 8, 2);
                break;
            case DeviceType.Controller:
                Rounded(2, 6, 20, 13, 5);
                g.DrawLine(p, 8, 9, 8, 15); g.DrawLine(p, 5, 12, 11, 12);
                g.FillEllipse(brush, 14, 10, 2, 2); g.FillEllipse(brush, 17, 13, 2, 2);
                break;
            default:
                Rounded(2, 6, 18, 12, 2.5f);
                g.DrawLine(p, 22, 10, 22, 14);
                g.FillRectangle(brush, 5, 9, 10, 6);
                break;
        }
        g.Restore(state);
    }
}
