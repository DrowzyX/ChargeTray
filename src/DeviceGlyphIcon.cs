using System.Drawing.Drawing2D;

namespace ChargeTray;

// The large Device Hub shares exactly the same vector device symbols as the
// compact tray panel. Icons remain crisp at Windows DPI 100-200%.
internal sealed class DeviceGlyphIcon : Control {
    private readonly DeviceType _type;

    internal DeviceGlyphIcon(DeviceType type) {
        _type = type;
        Size = new Size(38, 38);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        FluentStyle.DrawRounded(g, new Rectangle(0, 0, Width - 1, Height - 1),
            FluentStyle.SurfaceHover, 11, false);
        float side = Math.Min(25f, Math.Min(Width, Height) * .65f);
        var rect = new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side);
        Shapes.Glyph(g, _type, rect, FluentStyle.Text, 1.8f);
    }
}
