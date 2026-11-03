using System.Drawing.Drawing2D;

namespace InputTranslate;

static class Theme
{
    public static readonly Color Background = Color.FromArgb(15, 23, 42);
    public static readonly Color BackgroundDeep = Color.FromArgb(11, 18, 32);
    public static readonly Color Border = Color.FromArgb(45, 74, 105);
    public static readonly Color Accent = Color.FromArgb(56, 189, 248);
    public static readonly Color Text = Color.FromArgb(241, 245, 249);
    public static readonly Color Body = Color.FromArgb(203, 213, 225);
    public static readonly Color Muted = Color.FromArgb(148, 163, 184);
    public static readonly Color Dim = Color.FromArgb(100, 116, 139);
    public static readonly Color BadgeBack = Color.FromArgb(30, 58, 138);
    public static readonly Color BadgeText = Color.FromArgb(191, 219, 254);
    public static readonly Color Error = Color.FromArgb(248, 113, 113);

    public static int Scale(Control control, int value) =>
        (int)Math.Round(value * control.DeviceDpi / 96.0);

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

sealed class RoundButton : Button
{
    bool _hover;
    bool _pressed;

    public RoundButton()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        TabStop = false;
        Cursor = Cursors.Hand;
        Font = new Font("Microsoft YaHei UI", 9F);
        NormalBack = Color.FromArgb(30, 41, 59);
        HoverBack = Color.FromArgb(45, 60, 82);
        PressedBack = Color.FromArgb(20, 30, 46);
        BorderColor = Color.FromArgb(51, 65, 85);
        HoverBorderColor = Color.FromArgb(56, 189, 248);
        TextColor = Theme.Text;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 9;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color NormalBack { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color HoverBack { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color PressedBack { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color HoverBorderColor { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color TextColor { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(bounds, CornerRadius);
        using var back = new SolidBrush(_pressed ? PressedBack : _hover ? HoverBack : NormalBack);
        g.FillPath(back, path);
        using var pen = new Pen(_hover ? HoverBorderColor : BorderColor);
        g.DrawPath(pen, path);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            bounds,
            TextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }
}

