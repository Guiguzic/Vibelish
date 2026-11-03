namespace InputTranslate;

/// <summary>
/// 画出英文译文。鼠标移到单词上会高亮，点一下就查这个词；拖动超过几像素仍是在拖卡片。
/// </summary>
sealed class ClickableEnglish : Control
{
    readonly EnglishLayout _layout = new();
    string _english = "";
    int _hover = -1;
    int _down = -1;

    public ClickableEnglish()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        Cursor = Cursors.Default;
        TabStop = false;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string English
    {
        get => _english;
        set
        {
            value ??= "";
            if (_english == value)
                return;
            _english = value;
            _hover = -1;
            _down = -1;
            _layout.Build(_english, Font, Math.Max(10, Width));
            Invalidate();
        }
    }

    bool _lookupEnabled;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool LookupEnabled
    {
        get => _lookupEnabled;
        set
        {
            if (_lookupEnabled == value)
                return;
            _lookupEnabled = value;
            _hover = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    /// <summary>当前查释义的单词，画一条下划线标出来。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Marked { get; set; } = "";

    /// <summary>鼠标抬起时若卡片已经在拖，就不要当成点词。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool>? SuppressClick { get; set; }

    bool _loading;
    bool _caret;
    int _phase;

    /// <summary>还没有译文：画两条会呼吸的占位条。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Loading
    {
        get => _loading;
        set
        {
            if (_loading == value)
                return;
            _loading = value;
            Invalidate();
        }
    }

    /// <summary>逐字输出中：在最后一个字后面画一个闪烁的光标。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Caret
    {
        get => _caret;
        set
        {
            if (_caret == value)
                return;
            _caret = value;
            Invalidate();
        }
    }

    /// <summary>动画节拍（卡片的计时器每 90 毫秒调一次）。</summary>
    public void Tick()
    {
        if (!_loading && !_caret)
            return;
        _phase = (_phase + 1) % 24;
        Invalidate();
    }

    public event Action<string>? WordClicked;

    public Size Measure(int width)
    {
        if (_english.Length == 0 && _loading)
            return new Size(Math.Max(10, width), Font.Height * 2 + Font.Height / 3);
        _layout.Build(_english, Font, Math.Max(10, width));
        return new Size(
            Math.Min(Math.Max(10, width), Math.Max(_layout.Width, 1)),
            Math.Max(_layout.Height, Font?.Height ?? 16));
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _layout.Build(_english, Font, Math.Max(10, Width));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _layout.Build(_english, Font, Math.Max(10, Width));
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_layout.Runs.Count == 0 && _english.Length > 0)
            _layout.Build(_english, Font, Math.Max(10, Width));

        var p = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var breathe = 0.5 + 0.5 * Math.Cos(_phase * Math.PI / 12);
        if (_english.Length == 0)
        {
            if (!_loading)
                return;
            // 骨架占位：等第一个词的时候不显示空白。
            var bar = Math.Max(6, Font.Height * 3 / 5);
            var color = Theme.Mix(p.Fill, p.Card, 0.35 * breathe);
            using var brush = new SolidBrush(color);
            var rows = new[] { 0.92, 0.58 };
            for (var r = 0; r < rows.Length; r++)
            {
                var top = r * (Font.Height + Font.Height / 3) + (Font.Height - bar) / 2;
                using var path = Theme.RoundedRect(new Rectangle(0, top, (int)(Width * rows[r]), bar), bar / 2);
                g.FillPath(brush, path);
            }

            return;
        }
        // 每段的宽度是真实步进宽度，字形的悬垂会超出一点，不能裁掉。
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
            | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoClipping;
        var radius = Math.Max(2, Font.Height / 6);
        for (var i = 0; i < _layout.Runs.Count; i++)
        {
            var run = _layout.Runs[i];
            if (run.Text == "\n")
                continue;
            var hover = LookupEnabled && run.Word && i == _hover;
            var marked = LookupEnabled && run.Word && Marked.Length > 0
                && string.Equals(run.Text, Marked, StringComparison.OrdinalIgnoreCase);
            if (hover || marked)
            {
                var box = Rectangle.Inflate(run.Bounds, 2, 0);
                using var path = Theme.RoundedRect(box, radius);
                using var fill = new SolidBrush(p.AccentSoft);
                g.FillPath(fill, path);
            }

            TextRenderer.DrawText(g, run.Text, Font, run.Bounds, ForeColor, flags);
            if (hover || marked)
            {
                using var pen = new Pen(p.Accent, Math.Max(1, Font.Height / 14f));
                var y = run.Bounds.Bottom - 2;
                g.DrawLine(pen, run.Bounds.Left, y, run.Bounds.Right - 1, y);
            }
        }

        if (_caret && _layout.Runs.Count > 0 && breathe > 0.35)
        {
            var last = _layout.Runs[^1];
            var w = Math.Max(2, Font.Height / 12);
            var h = last.Bounds.Height * 3 / 4;
            using var caret = new SolidBrush(p.Accent);
            g.FillRectangle(caret, last.Bounds.Right + Math.Max(2, w), last.Bounds.Top + (last.Bounds.Height - h) / 2, w, h);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = LookupEnabled ? _layout.IndexAt(e.Location) : -1;
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
        if (_hover >= 0)
        {
            _hover = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _down = e.Button == MouseButtons.Left && LookupEnabled ? _layout.IndexAt(e.Location) : -1;
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        // 双击会再来一次 MouseUp。清掉按下的词，避免刚打开释义又被关掉。
        _down = -1;
        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var index = _down;
        _down = -1;
        var suppress = SuppressClick?.Invoke() == true;
        if (!suppress
            && e.Button == MouseButtons.Left
            && index >= 0
            && index < _layout.Runs.Count
            && _layout.Runs[index].Word
            && _layout.IndexAt(e.Location) == index)
        {
            WordClicked?.Invoke(_layout.Runs[index].Text);
        }

        base.OnMouseUp(e);
    }
}
