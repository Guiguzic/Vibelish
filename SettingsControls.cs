using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace InputTranslate;

// 设置窗口用的一组自绘控件：开关、分段选择、下拉按钮、输入框、分组列表、侧栏。
// 都在绘制时读 Theme.Current，换主题后 Invalidate 一下就跟着变。

abstract class ThemedControl : Control
{
    protected ThemedControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,
            true);
    }

    protected int Sc(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected static Palette P => Theme.Current;

    /// <summary>父控件画出来的底色（分组卡片是白的，页面是灰的）。</summary>
    protected Color Backdrop
    {
        get
        {
            for (var c = Parent; c != null; c = c.Parent)
            {
                if (c is SettingsGroup or SettingsRow)
                    return P.Group;
                if (c.BackColor.A == 255)
                    return c.BackColor;
            }

            return P.Window;
        }
    }

    protected void DrawFocus(Graphics g, Rectangle bounds, int radius)
    {
        if (!Focused || !ShowFocusCues)
            return;
        using var path = Theme.RoundedRect(Rectangle.Inflate(bounds, 2, 2), radius + 2);
        using var pen = new Pen(Color.FromArgb(150, P.Accent), Math.Max(2, Sc(2)));
        g.DrawPath(pen, path);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }
}

/// <summary>iOS / macOS 那种开关，带一点滑动动画。</summary>
sealed class ToggleSwitch : ThemedControl
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 12 };
    bool _checked;
    float _pos;

    public ToggleSwitch()
    {
        TabStop = true;
        Cursor = Cursors.Hand;
        _timer.Tick += (_, _) =>
        {
            var target = _checked ? 1f : 0f;
            _pos += (target - _pos) * 0.35f;
            if (Math.Abs(target - _pos) < 0.02f)
            {
                _pos = target;
                _timer.Stop();
            }

            Invalidate();
        };
    }

    public event EventHandler? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            if (IsHandleCreated && Visible)
                _timer.Start();
            else
                _pos = value ? 1 : 0;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Size Preferred => new(Sc(38), Sc(22));

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Backdrop;
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(1, 1, Width - 3, Height - 3);
        var on = Theme.Mix(P.SwitchOff, P.Accent, _pos);
        if (!Enabled)
            on = Theme.Mix(on, back, 0.55);
        using (var path = Theme.RoundedRect(track, track.Height / 2))
        using (var fill = new SolidBrush(on))
            g.FillPath(fill, path);

        var d = track.Height - Sc(4);
        var x = track.Left + Sc(2) + (int)Math.Round((track.Width - d - Sc(4)) * _pos);
        var y = track.Top + Sc(2);
        using (var shadow = new SolidBrush(Color.FromArgb(P.Dark ? 70 : 40, 0, 0, 0)))
            g.FillEllipse(shadow, x, y + 1, d, d);
        using (var knob = new SolidBrush(Enabled ? P.Knob : Theme.Mix(P.Knob, back, 0.3)))
            g.FillEllipse(knob, x, y, d, d);
        DrawFocus(g, track, track.Height / 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>分段选择：几个互斥的选项排成一条，选中的那段浮起来。</summary>
sealed class SegmentedControl : ThemedControl
{
    string[] _items = Array.Empty<string>();
    int _selected;
    int _hover = -1;

    public SegmentedControl(params string[] items)
    {
        _items = items;
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = Theme.Ui(9F);
    }

    public event EventHandler? SelectedIndexChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Max(0, Math.Min(_items.Length - 1, value));
            if (_selected == value)
                return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    int SegmentWidth()
    {
        var widest = 0;
        foreach (var item in _items)
            widest = Math.Max(widest, TextRenderer.MeasureText(item, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width);
        return widest + Sc(26);
    }

    public Size Preferred => new(SegmentWidth() * Math.Max(1, _items.Length) + Sc(4), Sc(28));

    Rectangle Segment(int index)
    {
        var w = (Width - Sc(4)) / (float)Math.Max(1, _items.Length);
        var x = Sc(2) + (int)Math.Round(w * index);
        var right = Sc(2) + (int)Math.Round(w * (index + 1));
        return new Rectangle(x, Sc(2), right - x, Height - Sc(4) - 1);
    }

    int IndexAt(Point point)
    {
        for (var i = 0; i < _items.Length; i++)
        {
            if (Segment(i).Contains(point))
                return i;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = IndexAt(e.Location);
        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var index = IndexAt(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0)
            SelectedIndex = index;
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left)
            SelectedIndex--;
        else if (e.KeyCode == Keys.Right)
            SelectedIndex++;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Backdrop;
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var radius = Sc(7);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(bounds, radius))
        using (var fill = new SolidBrush(P.Fill))
            g.FillPath(fill, path);

        for (var i = 0; i < _items.Length; i++)
        {
            var segment = Segment(i);
            if (i == _selected)
            {
                var lift = P.Dark ? Color.FromArgb(99, 99, 102) : Color.White;
                using var path = Theme.RoundedRect(segment, radius - Sc(2));
                using (var shadow = new SolidBrush(Color.FromArgb(P.Dark ? 60 : 22, 0, 0, 0)))
                {
                    using var shadowPath = Theme.RoundedRect(new Rectangle(segment.X, segment.Y + 1, segment.Width, segment.Height), radius - Sc(2));
                    g.FillPath(shadow, shadowPath);
                }

                using var fill = new SolidBrush(Enabled ? lift : Theme.Mix(lift, P.Fill, 0.5));
                g.FillPath(fill, path);
            }
            else if (i == _hover && Enabled)
            {
                using var path = Theme.RoundedRect(segment, radius - Sc(2));
                using var fill = new SolidBrush(P.FillHover);
                g.FillPath(fill, path);
            }

            var color = !Enabled ? P.Dim : i == _selected ? P.Text : P.Body;
            TextRenderer.DrawText(g, _items[i], i == _selected ? Theme.Ui(Font.SizeInPoints, FontStyle.Bold) : Font, segment, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        DrawFocus(g, bounds, radius);
    }
}

/// <summary>下拉按钮：显示当前选项和一个小箭头，点开是一列菜单。</summary>
sealed class PopupButton : ThemedControl
{
    readonly string[] _items;
    int _selected;
    bool _hover;

    public PopupButton(params string[] items)
    {
        _items = items;
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = Theme.Ui(9F);
    }

    public event EventHandler? SelectedIndexChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Max(0, Math.Min(_items.Length - 1, value));
            if (_selected == value)
                return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Size Preferred
    {
        get
        {
            var widest = 0;
            foreach (var item in _items)
                widest = Math.Max(widest, TextRenderer.MeasureText(item, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width);
            return new Size(widest + Sc(48), Sc(28));
        }
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
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Open();
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.Down)
        {
            Open();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    void Open()
    {
        var menu = ThemedMenu.Create();
        for (var i = 0; i < _items.Length; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(_items[i]) { Checked = i == _selected };
            item.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(item);
        }

        menu.MinimumSize = new Size(Width, 0);
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, new Point(0, Height + Sc(2)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Backdrop);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Sc(7);
        using (var path = Theme.RoundedRect(bounds, radius))
        using (var fill = new SolidBrush(_hover && Enabled ? P.FillHover : P.Fill))
            g.FillPath(fill, path);

        var pad = Sc(10);
        var text = _items.Length == 0 ? "" : _items[_selected];
        TextRenderer.DrawText(g, text, Font, new Rectangle(pad, 0, Width - pad - Sc(26), Height), Enabled ? P.Text : P.Dim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (Theme.Icons(7.5F) is { } icons)
        {
            TextRenderer.DrawText(g, Glyphs.ChevronDown, icons, new Rectangle(Width - Sc(24), 0, Sc(16), Height), P.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        DrawFocus(g, bounds, radius);
    }
}

/// <summary>圆角输入框：里面放一个无边框的 TextBox，聚焦时边框变成强调色。</summary>
sealed class TextField : ThemedControl
{
    const int EmSetCueBanner = 0x1501;

    readonly TextBox _box;
    readonly RoundButton? _trailing;
    string _placeholder = "";

    public TextField(TextBox? box = null, string trailingGlyph = "")
    {
        _box = box ?? new TextBox();
        _box.BorderStyle = BorderStyle.None;
        _box.Font = Theme.Ui(9F);
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _box.TextChanged += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty);
        _box.HandleCreated += (_, _) => ApplyPlaceholder();
        Controls.Add(_box);
        if (trailingGlyph.Length > 0)
        {
            _trailing = new RoundButton { Variant = ButtonVariant.Ghost, Glyph = trailingGlyph, CornerRadius = 6, Font = Theme.Ui(9F) };
            _trailing.Click += (_, _) => TrailingClicked?.Invoke(this, EventArgs.Empty);
            Controls.Add(_trailing);
        }

        Cursor = Cursors.IBeam;
        ApplyTheme();
    }

    public TextBox Box => _box;

    public new event EventHandler? TextChanged;

    public event EventHandler? TrailingClicked;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public override string Text
    {
        get => _box.Text;
#pragma warning disable CS8765
        set => _box.Text = value ?? "";
#pragma warning restore CS8765
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            _placeholder = value ?? "";
            ApplyPlaceholder();
        }
    }

    public void ApplyTheme()
    {
        _box.BackColor = P.Field;
        _box.ForeColor = P.Text;
        Invalidate(true);
    }

    void ApplyPlaceholder()
    {
        if (_box.IsHandleCreated)
            _ = SendMessage(_box.Handle, EmSetCueBanner, (IntPtr)1, _placeholder);
    }

    protected override void OnClick(EventArgs e)
    {
        _box.Focus();
        base.OnClick(e);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var pad = Sc(9);
        var trailing = 0;
        if (_trailing != null)
        {
            var size = Height - Sc(6);
            _trailing.SetBounds(Width - size - Sc(3), Sc(3), size, size);
            trailing = size + Sc(4);
        }

        var h = _box.PreferredHeight;
        _box.SetBounds(pad, Math.Max(1, (Height - h) / 2 + 1), Math.Max(10, Width - pad * 2 - trailing), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Backdrop);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Sc(7);
        using (var path = Theme.RoundedRect(bounds, radius))
        {
            using var fill = new SolidBrush(P.Field);
            g.FillPath(fill, path);
            var focused = _box.Focused;
            using var pen = new Pen(focused ? P.Accent : P.FieldBorder, focused ? Math.Max(1.5f, Sc(2) * 0.75f) : 1f);
            g.DrawPath(pen, path);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string lParam);
}

/// <summary>一组设置：白底圆角卡片，一行一个选项，行之间有细分隔线。</summary>
sealed class SettingsGroup : ThemedControl
{
    readonly List<SettingsRow> _rows = new();

    public SettingsGroup()
    {
        TabStop = false;
        BackColor = P.Group;
    }

    public void ApplyTheme()
    {
        BackColor = P.Group;
        foreach (var row in _rows)
            row.ApplyTheme();
        Invalidate(true);
    }

    public SettingsRow AddRow(string title, string subtitle, Control? control)
    {
        var row = new SettingsRow(title, subtitle, control);
        _rows.Add(row);
        Controls.Add(row);
        return row;
    }

    public IReadOnlyList<SettingsRow> Rows => _rows;

    /// <summary>按宽度排好每一行，返回总高度。</summary>
    public int Arrange(int width)
    {
        var y = 0;
        foreach (var row in _rows)
        {
            if (!row.Visible)
                continue;
            var height = row.Arrange(width);
            row.SetBounds(0, y, width, height);
            y += height;
        }

        return Math.Max(y, Sc(10));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintSurface(e.Graphics, Point.Empty);
    }

    /// <summary>画卡片底色、圆角、描边和行间分隔线。每一行也用它画自己那一块，
    /// 这样行盖在卡片上时圆角和分隔线还在。</summary>
    internal void PaintSurface(Graphics g, Point offset)
    {
        g.Clear(Parent?.BackColor ?? P.Window);
        var state = g.Save();
        g.TranslateTransform(-offset.X, -offset.Y);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(bounds, Sc(10)))
        {
            using var fill = new SolidBrush(P.Group);
            g.FillPath(fill, path);
            using var pen = new Pen(P.Dark ? P.Separator : Theme.Mix(P.Separator, P.Window, 0.35));
            g.DrawPath(pen, path);
        }

        g.SmoothingMode = SmoothingMode.None;
        using var line = new Pen(P.Separator);
        var first = true;
        foreach (var row in _rows)
        {
            if (!row.Visible)
                continue;
            if (!first)
                g.DrawLine(line, Sc(14), row.Top, Width - Sc(14), row.Top);
            first = false;
        }

        g.Restore(state);
    }
}

/// <summary>分组里的一行：左边标题和说明，右边控件。</summary>
sealed class SettingsRow : ThemedControl
{
    readonly TextLabel _title = new();
    readonly TextLabel _subtitle = new();

    public SettingsRow(string title, string subtitle, Control? control)
    {
        TabStop = false;
        Control = control;
        _title.Text = title;
        _subtitle.Text = subtitle;
        Controls.Add(_title);
        Controls.Add(_subtitle);
        if (control != null)
            Controls.Add(control);
        ApplyTheme();
    }

    public Control? Control { get; }

    /// <summary>控件占满整行（例如长说明、按钮条），标题放在上面。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Stacked { get; set; }

    /// <summary>右边控件的固定宽度；0 表示用控件自己的大小。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ControlWidth { get; set; }

    /// <summary>标题下面的说明。改了以后整页重新排（行高可能变）。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Subtitle
    {
        get => _subtitle.Text;
        set
        {
            _subtitle.Text = value ?? "";
            Parent?.Parent?.PerformLayout();
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color SubtitleColor
    {
        get => _subtitle.ForeColor;
        set => _subtitle.ForeColor = value;
    }

    public void ApplyTheme()
    {
        BackColor = P.Group;
        _title.Font = Theme.Ui(9.5F);
        _title.ForeColor = P.Text;
        _subtitle.Font = Theme.Ui(8.25F);
        _subtitle.ForeColor = P.Muted;
        Invalidate(true);
    }

    public int Arrange(int width)
    {
        var padX = Sc(14);
        var padY = Sc(11);
        var gap = Sc(14);
        var control = Control;
        var controlSize = Size.Empty;
        if (control != null)
        {
            controlSize = control switch
            {
                ToggleSwitch toggle => toggle.Preferred,
                SegmentedControl segmented => segmented.Preferred,
                PopupButton popup => popup.Preferred,
                AccentPicker accent => accent.Preferred,
                TextField => new Size(Sc(300), Sc(28)),
                _ => control.Size,
            };
            if (ControlWidth > 0)
                controlSize.Width = Sc(ControlWidth);
        }

        var hasTitle = _title.Text.Length > 0;
        var hasSubtitle = _subtitle.Text.Length > 0;
        if (Stacked && control != null)
        {
            var inner = width - padX * 2;
            var y = padY;
            if (hasTitle)
            {
                var t = _title.Measure(inner);
                _title.SetBounds(padX, y, inner, t.Height);
                y += t.Height + Sc(6);
            }

            _title.Visible = hasTitle;
            if (control is TextLabel label)
                controlSize = new Size(inner, label.Measure(inner).Height);
            else
                controlSize.Width = Math.Min(inner, controlSize.Width);
            control.SetBounds(padX, y, controlSize.Width, controlSize.Height);
            y += controlSize.Height;
            if (hasSubtitle)
            {
                y += Sc(6);
                var s = _subtitle.Measure(inner);
                _subtitle.SetBounds(padX, y, inner, s.Height);
                y += s.Height;
            }

            _subtitle.Visible = hasSubtitle;
            return y + padY;
        }

        var textWidth = Math.Max(Sc(80), width - padX * 2 - (control == null ? 0 : controlSize.Width + gap));
        var titleSize = hasTitle ? _title.Measure(textWidth) : Size.Empty;
        var subtitleSize = hasSubtitle ? _subtitle.Measure(textWidth) : Size.Empty;
        var textHeight = titleSize.Height + (hasSubtitle ? Sc(2) + subtitleSize.Height : 0);
        var height = Math.Max(Sc(44), Math.Max(textHeight, controlSize.Height) + padY * 2);
        var top = (height - textHeight) / 2;
        _title.Visible = hasTitle;
        _subtitle.Visible = hasSubtitle;
        _title.SetBounds(padX, top, textWidth, titleSize.Height);
        if (hasSubtitle)
            _subtitle.SetBounds(padX, top + titleSize.Height + Sc(2), textWidth, subtitleSize.Height);
        if (control != null)
            control.SetBounds(width - padX - controlSize.Width, (height - controlSize.Height) / 2, controlSize.Width, controlSize.Height);
        return height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Parent is SettingsGroup group)
            group.PaintSurface(e.Graphics, Location);
        else
            e.Graphics.Clear(P.Group);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        Invalidate();
    }
}

/// <summary>侧栏导航。</summary>
sealed class NavList : ThemedControl
{
    readonly List<(string Glyph, string Text)> _items = new();
    int _selected;
    int _hover = -1;

    public NavList()
    {
        TabStop = true;
        Font = Theme.Ui(9.5F);
    }

    public event EventHandler? SelectedIndexChanged;

    public void Add(string glyph, string text) => _items.Add((glyph, text));

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Max(0, Math.Min(_items.Count - 1, value));
            if (_selected == value)
                return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    int ItemHeight => Sc(34);

    public int PreferredHeight => _items.Count * (ItemHeight + Sc(2));

    Rectangle ItemBounds(int index) => new(Sc(10), index * (ItemHeight + Sc(2)), Width - Sc(20), ItemHeight);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            if (ItemBounds(i).Contains(e.Location))
                index = i;
        }

        if (index != _hover)
        {
            _hover = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _hover >= 0)
            SelectedIndex = _hover;
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up)
            SelectedIndex--;
        else if (e.KeyCode == Keys.Down)
            SelectedIndex++;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor.A == 255 ? BackColor : P.Sidebar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var icons = Theme.Icons(10.5F);
        for (var i = 0; i < _items.Count; i++)
        {
            var bounds = ItemBounds(i);
            var selected = i == _selected;
            if (selected || i == _hover)
            {
                using var path = Theme.RoundedRect(bounds, Sc(7));
                using var fill = new SolidBrush(selected ? P.Accent : P.FillHover);
                g.FillPath(fill, path);
            }

            var color = selected ? Color.White : P.Text;
            var x = bounds.Left + Sc(12);
            if (icons != null)
            {
                TextRenderer.DrawText(g, _items[i].Glyph, icons, new Rectangle(x, bounds.Top, Sc(20), bounds.Height),
                    selected ? Color.White : P.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += Sc(30);
            }

            TextRenderer.DrawText(g, _items[i].Text, Font, new Rectangle(x, bounds.Top, bounds.Right - x - Sc(8), bounds.Height), color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        if (Focused && ShowFocusCues && _selected >= 0 && _selected < _items.Count)
        {
            using var path = Theme.RoundedRect(Rectangle.Inflate(ItemBounds(_selected), 1, 1), Sc(8));
            using var pen = new Pen(Color.FromArgb(150, P.Accent), 2);
            g.DrawPath(pen, path);
        }
    }
}

/// <summary>强调色选择：一排圆点。</summary>
sealed class AccentPicker : ThemedControl
{
    int _selected;
    int _hover = -1;

    public AccentPicker()
    {
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? SelectedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Selected
    {
        get => Theme.AccentKeys[_selected];
        set
        {
            var index = Math.Max(0, Array.IndexOf(Theme.AccentKeys, value));
            if (index == _selected)
                return;
            _selected = index;
            Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    int Dot => Sc(18);

    int Step => Sc(28);

    public Size Preferred => new(Step * Theme.AccentKeys.Length - (Step - Dot) + Sc(6), Sc(26));

    Rectangle DotBounds(int index) => new(Sc(3) + index * Step, (Height - Dot) / 2, Dot, Dot);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = -1;
        for (var i = 0; i < Theme.AccentKeys.Length; i++)
        {
            if (Rectangle.Inflate(DotBounds(i), Sc(4), Sc(4)).Contains(e.Location))
                index = i;
        }

        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (_hover >= 0)
            Selected = Theme.AccentKeys[_hover];
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left && _selected > 0)
            Selected = Theme.AccentKeys[_selected - 1];
        else if (e.KeyCode == Keys.Right && _selected < Theme.AccentKeys.Length - 1)
            Selected = Theme.AccentKeys[_selected + 1];
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Backdrop);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = 0; i < Theme.AccentKeys.Length; i++)
        {
            var dot = DotBounds(i);
            var color = Theme.AccentColor(Theme.AccentKeys[i], P.Dark);
            if (i == _hover && i != _selected)
                dot.Inflate(1, 1);
            using (var fill = new SolidBrush(color))
                g.FillEllipse(fill, dot);
            if (i == _selected)
            {
                var d = Math.Max(4, Dot / 3);
                using var white = new SolidBrush(Color.White);
                g.FillEllipse(white, dot.Left + (dot.Width - d) / 2f, dot.Top + (dot.Height - d) / 2f, d, d);
                using var ring = new Pen(Color.FromArgb(120, color), Math.Max(1.5f, Sc(2) * 0.8f));
                g.DrawEllipse(ring, Rectangle.Inflate(dot, Sc(3), Sc(3)));
            }
        }

        if (Focused && ShowFocusCues)
            DrawFocus(g, Rectangle.Inflate(DotBounds(_selected), Sc(3), Sc(3)), Dot);
    }
}

/// <summary>跟随主题的右键 / 下拉菜单。</summary>
static class ThemedMenu
{
    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            Font = Theme.Ui(9F),
            Renderer = new ToolStripProfessionalRenderer(new MenuColors(Theme.Current)) { RoundedEdges = false },
            ShowImageMargin = true,
            BackColor = Theme.Current.Group,
            ForeColor = Theme.Current.Text,
        };
        menu.ItemAdded += (_, e) =>
        {
            if (e.Item == null)
                return;
            e.Item.ForeColor = Theme.Current.Text;
            e.Item.Padding = new Padding(e.Item.Padding.Left, 3, e.Item.Padding.Right, 3);
        };
        return menu;
    }

    public sealed class MenuColors : ProfessionalColorTable
    {
        readonly Palette _p;

        public MenuColors(Palette palette)
        {
            _p = palette;
            UseSystemColors = false;
        }

        Color Hover => _p.Selected;

        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color MenuBorder => _p.Separator;
        public override Color ToolStripDropDownBackground => _p.Group;
        public override Color ImageMarginGradientBegin => _p.Group;
        public override Color ImageMarginGradientMiddle => _p.Group;
        public override Color ImageMarginGradientEnd => _p.Group;
        public override Color SeparatorDark => _p.Separator;
        public override Color SeparatorLight => _p.Group;
        public override Color CheckBackground => _p.Group;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}

static class NativeTheme
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>滚动条等系统部件跟着深色 / 浅色走。</summary>
    public static void Apply(Control control, bool dark)
    {
        if (!control.IsHandleCreated)
            return;
        try
        {
            _ = SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch
        {
            // Ignore.
        }
    }

    /// <summary>标题栏：深色模式，以及 Windows 11 上和窗口底色一样的颜色。</summary>
    public static void TitleBar(Form form, Palette p)
    {
        if (!form.IsHandleCreated)
            return;
        try
        {
            var dark = p.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                var caption = Theme.ColorRef(p.Sidebar);
                _ = DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
                var text = Theme.ColorRef(p.Text);
                _ = DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
