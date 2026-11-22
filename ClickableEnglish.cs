namespace Vibelish;

/// <summary>
/// 画出英文译文。鼠标移到单词上会高亮，点一下就查这个词；拖动超过几像素仍是在拖卡片。
/// 有句子拆解时，按成分给句子上色、画下划线，并在每块下面写上它是什么（主语、谓语、宾语从句……）。
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
            MapSpans();
            Rebuild(Math.Max(10, Width));
            Invalidate();
        }
    }

    readonly record struct Span(int Start, int End, string Role);

    IReadOnlyList<SentencePart>? _parts;
    Language _labelLanguage = Languages.Find("zh-CN");
    readonly List<Span> _spans = new();
    int[] _runStarts = [];

    /// <summary>句子拆解：按成分上色、标名字。传 null 就恢复成普通文字。</summary>
    public void SetParts(IReadOnlyList<SentencePart>? parts, Language labelLanguage)
    {
        if (ReferenceEquals(parts, _parts) && labelLanguage == _labelLanguage)
            return;
        _parts = parts;
        _labelLanguage = labelLanguage;
        MapSpans();
        Rebuild(Math.Max(10, Width));
        Invalidate();
    }

    /// <summary>在句子里依次找每一块的位置（从上一块后面接着找，找不到就跳过这块）。</summary>
    void MapSpans()
    {
        _spans.Clear();
        if (_parts is not { Count: > 0 } parts || _english.Length == 0)
            return;
        var cursor = 0;
        foreach (var part in parts)
        {
            var text = part.Text.Trim().TrimEnd(',', '.', ';', '，', '。');
            if (text.Length == 0)
                continue;
            var at = _english.IndexOf(text, cursor, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                at = _english.IndexOf(text, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || _spans.Any(span => at < span.End && at + text.Length > span.Start))
                continue;
            _spans.Add(new Span(at, at + text.Length, part.Role));
            cursor = at + text.Length;
        }

        _spans.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    Font LabelFont => Theme.Card(7.5F);

    int LabelSpace => _spans.Count > 0 ? LabelFont.Height + 3 : 0;

    void Rebuild(int width)
    {
        _layout.Build(_english, Font, width, LabelSpace);
        // 每段文字在原句里的位置，用来判断它属于哪一块。
        _runStarts = new int[_layout.Runs.Count];
        var cursor = 0;
        for (var i = 0; i < _layout.Runs.Count; i++)
        {
            var text = _layout.Runs[i].Text;
            var at = text.Length == 0 ? -1 : _english.IndexOf(text, cursor, StringComparison.Ordinal);
            if (at < 0)
                at = cursor;
            _runStarts[i] = at;
            cursor = at + text.Length;
        }
    }

    int SpanOf(int run)
    {
        if (run >= _runStarts.Length)
            return -1;
        var start = _runStarts[run];
        var end = start + _layout.Runs[run].Text.Length;
        for (var i = 0; i < _spans.Count; i++)
        {
            if (start >= _spans[i].Start && end <= _spans[i].End)
                return i;
        }

        return -1;
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
        Rebuild(Math.Max(10, width));
        return new Size(
            Math.Min(Math.Max(10, width), Math.Max(_layout.Width, 1)),
            Math.Max(_layout.Height, Font?.Height ?? 16));
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Rebuild(Math.Max(10, Width));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild(Math.Max(10, Width));
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_layout.Runs.Count == 0 && _english.Length > 0)
            Rebuild(Math.Max(10, Width));

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
        var labelFont = LabelFont;
        var labelled = new HashSet<int>();
        var labelRight = new Dictionary<int, int>();
        var stroke = Math.Max(1.5f, Font.Height / 16f);
        for (var i = 0; i < _layout.Runs.Count; i++)
        {
            var run = _layout.Runs[i];
            if (run.Text == "\n")
                continue;
            var spanIndex = _spans.Count > 0 ? SpanOf(i) : -1;
            var roleColor = spanIndex >= 0 ? SentencePart.ColorFor(_spans[spanIndex].Role, p.Dark) : Color.Empty;
            if (spanIndex >= 0)
            {
                // 下划线连成一条：这块里的空格也画。
                using var line = new Pen(roleColor, stroke);
                var underline = run.Bounds.Bottom + 1;
                g.DrawLine(line, run.Bounds.Left, underline, run.Bounds.Right, underline);
                if (labelled.Add(spanIndex) && !string.IsNullOrWhiteSpace(run.Text))
                {
                    var label = SentencePart.Label(_spans[spanIndex].Role, _labelLanguage);
                    var size = TextRenderer.MeasureText(label, labelFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    var lineKey = run.Bounds.Y;
                    var x = Math.Max(run.Bounds.Left, labelRight.TryGetValue(lineKey, out var right) ? right + 4 : 0);
                    TextRenderer.DrawText(g, label, labelFont, new Point(x, underline + 2), roleColor, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    labelRight[lineKey] = x + size.Width;
                }
            }
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

            TextRenderer.DrawText(g, run.Text, Font, run.Bounds, spanIndex >= 0 && ForeColor != p.Dim ? roleColor : ForeColor, flags);
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
