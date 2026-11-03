using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Vibelish;

// 这个文件同时编进安装程序（.NET Framework 4.8），不要用 Index/Range、Math.Clamp 之类的新 API。

/// <summary>一套配色。深色和浅色各一份，强调色可换；控件在绘制时读取 <see cref="Theme.Current"/>。</summary>
sealed class Palette
{
    public bool Dark { get; set; }

    // 卡片
    public Color Card { get; set; }
    public Color CardBorder { get; set; }
    public Color Surface { get; set; }
    public Color SurfaceBorder { get; set; }

    // 文字：主要 → 正文 → 次要 → 最淡
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

    // 设置窗口
    public Color Window { get; set; }
    public Color Sidebar { get; set; }
    public Color Group { get; set; }
    public Color Separator { get; set; }
    public Color Fill { get; set; }
    public Color FillHover { get; set; }
    public Color Field { get; set; }
    public Color FieldBorder { get; set; }
    public Color SwitchOff { get; set; }
    public Color Knob { get; set; }

    public Palette Clone() => (Palette)MemberwiseClone();
}

static class Theme
{
    public static readonly Palette DarkPalette = new Palette
    {
        Dark = true,
        Card = Color.FromArgb(30, 30, 32),
        CardBorder = Color.FromArgb(58, 58, 61),
        Surface = Color.FromArgb(44, 44, 46),
        SurfaceBorder = Color.FromArgb(58, 58, 60),
        Text = Color.FromArgb(245, 245, 247),
        Body = Color.FromArgb(209, 209, 214),
        Muted = Color.FromArgb(152, 152, 157),
        Dim = Color.FromArgb(108, 108, 112),
        Success = Color.FromArgb(48, 209, 88),
        Warning = Color.FromArgb(255, 179, 64),
        Error = Color.FromArgb(255, 105, 97),
        ButtonBack = Color.FromArgb(50, 50, 53),
        ButtonHover = Color.FromArgb(62, 62, 66),
        ButtonPressed = Color.FromArgb(42, 42, 45),
        ButtonBorder = Color.FromArgb(50, 50, 53),
        ButtonText = Color.FromArgb(235, 235, 240),
        PrimaryText = Color.White,
        DangerHover = Color.FromArgb(80, 36, 36),
        Window = Color.FromArgb(28, 28, 30),
        Sidebar = Color.FromArgb(36, 36, 38),
        Group = Color.FromArgb(44, 44, 46),
        Separator = Color.FromArgb(58, 58, 60),
        Fill = Color.FromArgb(50, 50, 53),
        FillHover = Color.FromArgb(62, 62, 66),
        Field = Color.FromArgb(30, 30, 32),
        FieldBorder = Color.FromArgb(72, 72, 74),
        SwitchOff = Color.FromArgb(72, 72, 76),
        Knob = Color.White,
    };

    public static readonly Palette LightPalette = new Palette
    {
        Dark = false,
        Card = Color.White,
        CardBorder = Color.FromArgb(222, 222, 227),
        Surface = Color.FromArgb(245, 245, 247),
        SurfaceBorder = Color.FromArgb(232, 232, 237),
        Text = Color.FromArgb(29, 29, 31),
        Body = Color.FromArgb(58, 58, 60),
        Muted = Color.FromArgb(110, 110, 115),
        Dim = Color.FromArgb(161, 161, 166),
        Success = Color.FromArgb(36, 138, 61),
        Warning = Color.FromArgb(178, 106, 0),
        Error = Color.FromArgb(215, 44, 36),
        ButtonBack = Color.FromArgb(240, 240, 243),
        ButtonHover = Color.FromArgb(229, 229, 234),
        ButtonPressed = Color.FromArgb(216, 216, 222),
        ButtonBorder = Color.FromArgb(240, 240, 243),
        ButtonText = Color.FromArgb(29, 29, 31),
        PrimaryText = Color.White,
        DangerHover = Color.FromArgb(253, 231, 231),
        Window = Color.FromArgb(245, 245, 247),
        Sidebar = Color.FromArgb(236, 236, 240),
        Group = Color.White,
        Separator = Color.FromArgb(229, 229, 234),
        Fill = Color.FromArgb(240, 240, 243),
        FillHover = Color.FromArgb(229, 229, 234),
        Field = Color.White,
        FieldBorder = Color.FromArgb(210, 210, 215),
        SwitchOff = Color.FromArgb(220, 220, 225),
        Knob = Color.White,
    };

    /// <summary>可选的强调色（浅色 / 深色模式下各一个）。</summary>
    public static readonly string[] AccentKeys = { "blue", "purple", "pink", "orange", "green", "graphite" };

    static Theme()
    {
        WithAccent(LightPalette, "blue");
        WithAccent(DarkPalette, "blue");
    }

    static Palette _current = DarkPalette;
    static float _textScale = 1f;
    static string _accent = "blue";
    static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
    static HashSet<string>? _families;
    static string? _iconFamily;

    public static Palette Current => _current;

    public static string AccentKey => _accent;

    /// <summary>卡片文字缩放（较小 / 标准 / 较大）。</summary>
    public static float TextScale => _textScale;

    public const string CjkFamily = "Microsoft YaHei UI";

    /// <param name="mode">system、dark 或 light。</param>
    /// <param name="textSize">small、normal 或 large。</param>
    /// <param name="accent">见 <see cref="AccentKeys"/>。</param>
    public static void Apply(string? mode, string? textSize, string? accent = null)
    {
        var dark = (mode ?? "").ToLowerInvariant() switch
        {
            "light" => false,
            "dark" => true,
            _ => !SystemUsesLightTheme(),
        };
        _accent = Array.IndexOf(AccentKeys, (accent ?? "").ToLowerInvariant()) >= 0 ? accent!.ToLowerInvariant() : "blue";
        _current = WithAccent((dark ? DarkPalette : LightPalette).Clone(), _accent);
        _textScale = (textSize ?? "").ToLowerInvariant() switch
        {
            "small" => 0.9f,
            "large" => 1.15f,
            _ => 1f,
        };
    }

    /// <summary>强调色本身（设置页的色块用）。</summary>
    public static Color AccentColor(string key, bool dark)
    {
        switch (key)
        {
            case "purple": return dark ? Color.FromArgb(191, 90, 242) : Color.FromArgb(162, 72, 214);
            case "pink": return dark ? Color.FromArgb(255, 55, 95) : Color.FromArgb(232, 38, 84);
            case "orange": return dark ? Color.FromArgb(255, 159, 10) : Color.FromArgb(232, 121, 0);
            case "green": return dark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(40, 160, 72);
            case "graphite": return dark ? Color.FromArgb(152, 152, 157) : Color.FromArgb(120, 120, 126);
            default: return dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 113, 227);
        }
    }

    static Palette WithAccent(Palette p, string key)
    {
        var accent = AccentColor(key, p.Dark);
        p.Accent = accent;
        p.AccentSoft = Color.FromArgb(p.Dark ? 60 : 34, accent);
        p.PrimaryBack = accent;
        p.PrimaryHover = p.Dark ? Mix(accent, Color.White, 0.12) : Mix(accent, Color.Black, 0.08);
        p.PrimaryPressed = Mix(accent, Color.Black, p.Dark ? 0.12 : 0.18);
        p.Selected = Blend(Color.FromArgb(p.Dark ? 70 : 30, accent), p.Card);
        return p;
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

    /// <summary>英文正文字体：Windows 11 用 Segoe UI Variable，Windows 10 用 Segoe UI。</summary>
    public static Font Latin(float size, FontStyle style = FontStyle.Regular) =>
        Get(Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI", size, style);

    /// <summary>大号英文标题字体（半粗）。</summary>
    public static Font Display(float size)
    {
        if (Has("Segoe UI Variable Display Semib"))
            return Get("Segoe UI Variable Display Semib", size, FontStyle.Regular);
        if (Has("Segoe UI Semibold"))
            return Get("Segoe UI Semibold", size, FontStyle.Regular);
        return Get("Segoe UI", size, FontStyle.Bold);
    }

    /// <summary>卡片上的字体，跟随「文字大小」设置。</summary>
    public static Font Card(float size, FontStyle style = FontStyle.Regular) => Ui(size * _textScale, style);

    public static Font CardLatin(float size, FontStyle style = FontStyle.Regular) => Latin(size * _textScale, style);

    /// <summary>卡片上的英文译文。</summary>
    public static Font CardEnglish(float size) => Display(size * _textScale);

    /// <summary>卡片上某种语言的文字。family 为空时用英文字体；这台电脑没装这个字体就退回中文界面字体。</summary>
    public static Font CardText(string family, float size, FontStyle style = FontStyle.Regular) =>
        family.Length == 0 ? CardLatin(size, style) : Get(Has(family) ? family : CjkFamily, size * _textScale, style);

    /// <summary>Segoe Fluent Icons（Windows 11）或 Segoe MDL2 Assets（Windows 10）里的图标字体。</summary>
    public static Font? Icons(float size)
    {
        if (_iconFamily == null)
            _iconFamily = Has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : Has("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : "";
        return _iconFamily.Length == 0 ? null : Get(_iconFamily, size, FontStyle.Regular);
    }

    static bool Has(string family)
    {
        if (_families == null)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var installed = new InstalledFontCollection();
                foreach (var item in installed.Families)
                    names.Add(item.Name);
            }
            catch
            {
                // 读不到字体列表就全用默认字体。
            }

            _families = names;
        }

        return _families.Contains(family);
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

    /// <summary>把 a 往 b 混 amount（0–1）。</summary>
    public static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * amount),
        (int)Math.Round(a.G + (b.G - a.G) * amount),
        (int)Math.Round(a.B + (b.B - a.B) * amount));

    public static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
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
    public const string ChevronDown = "";
    public const string ChevronRight = "";
    public const string View = "";
    public const string Globe = "";
    public const string Bolt = "";
    public const string ColorPalette = "";
    public const string Dictionary = "";
    public const string Info = "";
    public const string Keyboard = "";
    public const string Folder = "";
    public const string Document = "";
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
    /// <summary>浅灰底，没有边框。</summary>
    Secondary,

    /// <summary>强调色底，白字。</summary>
    Primary,

    /// <summary>没有底色，悬停时才出现浅灰底。</summary>
    Ghost,

    /// <summary>像 Ghost，悬停时变红（关闭按钮）。</summary>
    Danger,

    /// <summary>强调色文字，像链接。</summary>
    Link,
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

    /// <summary>圆角半径；给一个很大的值就是胶囊形。</summary>
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

    /// <summary>底色以外的背景（父控件画的颜色）。为空时取父控件的 BackColor。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Backdrop { get; set; } = Color.Empty;

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

    Font? GlyphFont() => Glyph.Length == 0 ? null : Theme.Icons(Font.SizeInPoints * (string.IsNullOrEmpty(Text) ? 1.05f : 0.95f));

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = PaletteOverride ?? Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var parentBack = !Backdrop.IsEmpty ? Backdrop : Parent?.BackColor ?? p.Card;
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
            case ButtonVariant.Link:
                back = _pressed ? p.ButtonPressed : _hover ? Theme.Blend(p.AccentSoft, parentBack) : parentBack;
                border = back;
                text = _pressed ? p.PrimaryPressed : p.Accent;
                break;
            default:
                back = _pressed ? p.ButtonPressed : _hover ? p.ButtonHover : p.ButtonBack;
                border = p.ButtonBorder;
                text = p.ButtonText;
                break;
        }

        if (!Enabled)
        {
            text = p.Dim;
            if (Variant == ButtonVariant.Primary)
            {
                back = p.ButtonBack;
                border = p.ButtonBack;
            }
        }

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(bounds, Theme.Scale(this, CornerRadius)))
        {
            if (back != parentBack)
            {
                using var fill = new SolidBrush(back);
                g.FillPath(fill, path);
            }

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
