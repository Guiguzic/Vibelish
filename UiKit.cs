using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace InputTranslate;

// 这个文件同时编进安装程序（.NET Framework 4.8），不要用 Index/Range、Math.Clamp 之类的新 API。

/// <summary>一套卡片配色。深色和浅色各一份，控件在绘制时读取 <see cref="Theme.Current"/>。</summary>
sealed class Palette
{
    public bool Dark { get; set; }
    public Color Card { get; set; }
    public Color CardBorder { get; set; }
    public Color Surface { get; set; }
    public Color SurfaceBorder { get; set; }
    public Color Text { get; set; }
    public Color Body { get; set; }
    public Color Muted { get; set; }
    public Color Dim { get; set; }
    public Color Accent { get; set; }
    public Color AccentSoft { get; set; }
    public Color Success { get; set; }
    public Color Warning { get; set; }
    public Color Error { get; set; }
    public Color ButtonBack { get; set; }
    public Color ButtonHover { get; set; }
    public Color ButtonPressed { get; set; }
    public Color ButtonBorder { get; set; }
    public Color ButtonText { get; set; }
    public Color PrimaryBack { get; set; }
    public Color PrimaryHover { get; set; }
    public Color PrimaryPressed { get; set; }
    public Color PrimaryText { get; set; }
    public Color DangerHover { get; set; }
    public Color Selected { get; set; }
}

static class Theme
{
    public static readonly Palette DarkPalette = new()
    {
        Dark = true,
        Card = Color.FromArgb(22, 27, 38),
        CardBorder = Color.FromArgb(52, 63, 84),
        Surface = Color.FromArgb(30, 37, 51),
        SurfaceBorder = Color.FromArgb(55, 67, 89),
        Text = Color.FromArgb(243, 246, 250),
        Body = Color.FromArgb(203, 213, 225),
        Muted = Color.FromArgb(148, 163, 184),
        Dim = Color.FromArgb(118, 132, 155),
        Accent = Color.FromArgb(77, 171, 247),
        AccentSoft = Color.FromArgb(40, 77, 171, 247),
        Success = Color.FromArgb(74, 222, 128),
        Warning = Color.FromArgb(251, 191, 36),
        Error = Color.FromArgb(248, 113, 113),
        ButtonBack = Color.FromArgb(38, 46, 62),
        ButtonHover = Color.FromArgb(50, 60, 80),
        ButtonPressed = Color.FromArgb(31, 38, 52),
        ButtonBorder = Color.FromArgb(58, 70, 92),
        ButtonText = Color.FromArgb(226, 232, 240),
        PrimaryBack = Color.FromArgb(37, 128, 235),
        PrimaryHover = Color.FromArgb(56, 145, 245),
        PrimaryPressed = Color.FromArgb(29, 108, 204),
        PrimaryText = Color.White,
        DangerHover = Color.FromArgb(127, 29, 29),
        Selected = Color.FromArgb(33, 58, 92),
    };

    public static readonly Palette LightPalette = new()
    {
        Dark = false,
        Card = Color.FromArgb(255, 255, 255),
        CardBorder = Color.FromArgb(214, 221, 231),
        Surface = Color.FromArgb(244, 247, 251),
        SurfaceBorder = Color.FromArgb(222, 228, 237),
        Text = Color.FromArgb(17, 24, 39),
        Body = Color.FromArgb(55, 65, 81),
        Muted = Color.FromArgb(100, 112, 130),
        Dim = Color.FromArgb(120, 131, 148),
        Accent = Color.FromArgb(29, 112, 214),
        AccentSoft = Color.FromArgb(32, 29, 112, 214),
        Success = Color.FromArgb(22, 163, 74),
        Warning = Color.FromArgb(202, 138, 4),
        Error = Color.FromArgb(220, 38, 38),
        ButtonBack = Color.FromArgb(255, 255, 255),
        ButtonHover = Color.FromArgb(241, 245, 249),
        ButtonPressed = Color.FromArgb(226, 232, 240),
        ButtonBorder = Color.FromArgb(203, 213, 225),
        ButtonText = Color.FromArgb(30, 41, 59),
        PrimaryBack = Color.FromArgb(29, 112, 214),
        PrimaryHover = Color.FromArgb(24, 98, 192),
        PrimaryPressed = Color.FromArgb(20, 84, 166),
        PrimaryText = Color.White,
        DangerHover = Color.FromArgb(254, 226, 226),
        Selected = Color.FromArgb(225, 238, 253),
    };

    static Palette _current = DarkPalette;
    static float _textScale = 1f;
    static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
    static string? _iconFamily;

    public static Palette Current => _current;

    /// <summary>卡片文字缩放（较小 / 标准 / 较大）。</summary>
    public static float TextScale => _textScale;

    public const string CjkFamily = "Microsoft YaHei UI";
    public const string LatinFamily = "Segoe UI";

    /// <param name="mode">system、dark 或 light。</param>
    /// <param name="textSize">small、normal 或 large。</param>
    public static void Apply(string? mode, string? textSize)
    {
        _current = (mode ?? "").ToLowerInvariant() switch
        {
            "light" => LightPalette,
            "dark" => DarkPalette,
            _ => SystemUsesLightTheme() ? LightPalette : DarkPalette,
        };
        _textScale = (textSize ?? "").ToLowerInvariant() switch
        {
            "small" => 0.9f,
            "large" => 1.15f,
            _ => 1f,
        };
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>界面字体（中文）。同样的字号和样式只创建一次，程序退出前不释放。</summary>
    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => Get(CjkFamily, size, style);

    /// <summary>英文正文字体。</summary>
    public static Font Latin(float size, FontStyle style = FontStyle.Regular) => Get(LatinFamily, size, style);

    /// <summary>卡片上的字体，跟随「文字大小」设置。</summary>
    public static Font Card(float size, FontStyle style = FontStyle.Regular) => Ui(size * _textScale, style);

    public static Font CardLatin(float size, FontStyle style = FontStyle.Regular) => Latin(size * _textScale, style);

    /// <summary>卡片上的英文译文：Segoe UI Semibold，比 Bold 清爽。</summary>
    public static Font CardEnglish(float size) => Get("Segoe UI Semibold", size * _textScale, FontStyle.Regular);

    /// <summary>Segoe Fluent Icons（Windows 11）或 Segoe MDL2 Assets（Windows 10）里的图标字体。</summary>
    public static Font? Icons(float size)
    {
        if (_iconFamily == null)
        {
            _iconFamily = "";
            try
            {
                using var installed = new InstalledFontCollection();
                foreach (var family in installed.Families)
                {
                    if (family.Name == "Segoe Fluent Icons")
                    {
                        _iconFamily = family.Name;
                        break;
                    }

                    if (family.Name == "Segoe MDL2 Assets")
                        _iconFamily = family.Name;
                }
            }
            catch
            {
                _iconFamily = "";
            }
        }

        return _iconFamily.Length == 0 ? null : Get(_iconFamily, size, FontStyle.Regular);
    }

    static Font Get(string family, float size, FontStyle style)
    {
        size = (float)Math.Round(size * 4) / 4f;
        var key = family + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)style;
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(key, out var font))
            {
                font = new Font(family, size, style);
                Fonts[key] = font;
            }

            return font;
        }
    }

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

    public static Color Blend(Color top, Color bottom)
    {
        var a = top.A / 255f;
        return Color.FromArgb(
            (int)Math.Round(top.R * a + bottom.R * (1 - a)),
            (int)Math.Round(top.G * a + bottom.G * (1 - a)),
            (int)Math.Round(top.B * a + bottom.B * (1 - a)));
    }
}

/// <summary>常用的 Segoe Fluent Icons / MDL2 码位。</summary>
static class Glyphs
{
    public const string Copy = "";
    public const string Edit = "";
    public const string Settings = "";
    public const string Close = "";
    public const string Refresh = "";
    public const string Check = "";
    public const string Speaker = "";
}

/// <summary>量和画用同一套 NoPadding 规则的文字。Label 画的时候自带左右内边距，
/// 量出来的宽度装不下，单行标题会被截掉一截，也和旁边自绘的英文对不齐。</summary>
sealed class TextLabel : Control
{
    public TextLabel()
    {
        SetStyle(
            ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.Transparent;
        TabStop = false;
    }

    /// <summary>单行显示，放不下用省略号；否则按宽度自动换行。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SingleLine { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AlignRight { get; set; }

    TextFormatFlags Flags =>
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
        | (SingleLine ? TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter : TextFormatFlags.WordBreak)
        | (AlignRight ? TextFormatFlags.Right : TextFormatFlags.Left);

    /// <summary>在给定宽度里需要的尺寸。</summary>
    public Size Measure(int width)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | (SingleLine ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak);
        var size = TextRenderer.MeasureText(
            string.IsNullOrEmpty(Text) ? " " : Text,
            Font,
            new Size(Math.Max(10, width), int.MaxValue),
            flags);
        return new Size(size.Width + 2, Math.Max(size.Height, Font.Height));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
    }
}

enum ButtonVariant
{
    Secondary,
    Primary,
    Ghost,
    Danger,
}

/// <summary>圆角按钮。颜色默认取当前主题，也可以给一份固定配色（安装程序用浅色）。</summary>
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
        Font = Theme.Ui(9F);
        UseMnemonic = false;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 8;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ButtonVariant Variant { get; set; } = ButtonVariant.Secondary;

    /// <summary>固定配色；为 null 时跟随 <see cref="Theme.Current"/>。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Palette? PaletteOverride { get; set; }

    /// <summary>文字前的图标（图标字体码位），可以为空。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";

    /// <summary>按文字和图标算出合适的宽度。</summary>
    public int PreferredWidth(int padding)
    {
        var text = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        var icon = GlyphFont() is { } font && Glyph.Length > 0
            ? TextRenderer.MeasureText(Glyph, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width
            : 0;
        var gap = text > 0 && icon > 0 ? Theme.Scale(this, 6) : 0;
        return text + icon + gap + padding * 2;
    }

    Font? GlyphFont() => Glyph.Length == 0 ? null : Theme.Icons(Font.SizeInPoints * 0.95f);

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = PaletteOverride ?? Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var parentBack = Parent?.BackColor ?? p.Card;
        if (parentBack.A < 255)
            parentBack = p.Card;
        g.Clear(parentBack);

        Color back;
        Color border;
        Color text;
        switch (Variant)
        {
            case ButtonVariant.Primary:
                back = _pressed ? p.PrimaryPressed : _hover ? p.PrimaryHover : p.PrimaryBack;
                border = back;
                text = p.PrimaryText;
                break;
            case ButtonVariant.Ghost:
                back = _pressed ? p.ButtonPressed : _hover ? p.ButtonHover : parentBack;
                border = back;
                text = _hover ? p.Text : p.Muted;
                break;
            case ButtonVariant.Danger:
                back = _pressed ? p.ButtonPressed : _hover ? p.DangerHover : parentBack;
                border = back;
                text = _hover ? p.Error : p.Muted;
                break;
            default:
                back = _pressed ? p.ButtonPressed : _hover ? p.ButtonHover : p.ButtonBack;
                border = _hover ? p.Accent : p.ButtonBorder;
                text = p.ButtonText;
                break;
        }

        if (!Enabled)
        {
            text = p.Dim;
            if (Variant == ButtonVariant.Primary)
            {
                back = p.ButtonBack;
                border = p.ButtonBorder;
            }
        }

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(bounds, Theme.Scale(this, CornerRadius)))
        {
            using var fill = new SolidBrush(back);
            g.FillPath(fill, path);
            if (border != back)
            {
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
        }

        var glyphFont = GlyphFont();
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        if (glyphFont == null || Glyph.Length == 0)
        {
            TextRenderer.DrawText(g, Text, Font, bounds, text, flags | TextFormatFlags.HorizontalCenter);
            return;
        }

        var glyphSize = TextRenderer.MeasureText(Glyph, glyphFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        var textWidth = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        var gap = textWidth > 0 ? Theme.Scale(this, 6) : 0;
        var x = (Width - glyphSize.Width - gap - textWidth) / 2;
        TextRenderer.DrawText(g, Glyph, glyphFont, new Rectangle(x, 0, glyphSize.Width, Height), text, flags);
        if (textWidth > 0)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphSize.Width + gap, 0, textWidth + 2, Height), text, flags);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
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
