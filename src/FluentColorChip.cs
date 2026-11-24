using System.Drawing.Drawing2D;

namespace ChargeTray;

internal sealed class FluentColorChip : Control {
    internal Color BaseColor;
    internal bool Selected;
    private bool _hover;

    internal FluentColorChip(string label, Color color) {
        Text = label;
        BaseColor = color;
        Size = new Size(122, 40);
        Font = FluentStyle.Font(9);
        Cursor = Cursors.Hand;
        Margin = new Padding(3, 3, 3, 3);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    private static Color Blend(Color a, Color b, float weight) =>
        Color.FromArgb((int)(a.R * (1-weight) + b.R * weight),
            (int)(a.G * (1-weight) + b.G * weight),
            (int)(a.B * (1-weight) + b.B * weight));

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(FluentStyle.Surface);
        var bg = FluentStyle.IsDark
            ? Blend(BaseColor, FluentStyle.Surface, _hover ? .55f : .68f)
            : Blend(BaseColor, Color.White, _hover ? .54f : .72f);
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = FluentStyle.Round(bounds, 11);
        using var brush = new SolidBrush(bg);
        using var outline = new Pen(Selected ? AccentTheme.Accent :
            FluentStyle.IsDark ? Blend(BaseColor, FluentStyle.Border, .42f) :
            Blend(BaseColor, FluentStyle.Border, .68f), Selected ? 2f : 1f);
        g.FillPath(brush, path);
        g.DrawPath(outline, path);
        using var dot = new SolidBrush(BaseColor);
        g.FillEllipse(dot, 11, 14, 12, 12);
        using var dotBorder = new Pen(FluentStyle.IsDark ? Color.WhiteSmoke : Color.FromArgb(123, 135, 150), .7f);
        g.DrawEllipse(dotBorder, 11, 14, 12, 12);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(29, 1, Width - 37, Height - 2),
            FluentStyle.IsDark ? Color.White : Color.FromArgb(24, 31, 43),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Selected) {
            using var marker = new SolidBrush(AccentTheme.Accent);
            g.FillEllipse(marker, Width - 10, 6, 4, 4);
        }
    }
}
