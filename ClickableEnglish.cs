namespace InputTranslate;

/// <summary>
/// 画出英文译文。单词带下划线，点一下就查出这个词，拖动超过几像素仍是在拖卡片。
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
        BackColor = Color.FromArgb(17, 25, 40);
        ForeColor = Color.White;
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
            Invalidate();
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool LookupEnabled { get; set; }

    /// <summary>鼠标抬起时若卡片已经在拖，就不要当成点词。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool>? SuppressClick { get; set; }

    public event Action<string>? WordClicked;

    public Size Measure(int width)
    {
        _layout.Build(_english, Font, Math.Max(10, width));
        return new Size(
            Math.Min(Math.Max(10, width), Math.Max(_layout.Width, 1)),
            Math.Max(_layout.Height, Font?.Height ?? 16));
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

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
            | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        for (var i = 0; i < _layout.Runs.Count; i++)
        {
            var run = _layout.Runs[i];
            if (run.Text == "\n")
                continue;
            if (LookupEnabled && run.Word && i == _hover)
            {
                using var fill = new SolidBrush(Color.FromArgb(48, 56, 189, 248));
                e.Graphics.FillRectangle(fill, run.Bounds);
            }

            TextRenderer.DrawText(e.Graphics, run.Text, Font, run.Bounds, ForeColor, flags);
            if (LookupEnabled && run.Word && i == _hover)
            {
                using var pen = new Pen(Theme.Accent);
                var y = run.Bounds.Bottom - 2;
                e.Graphics.DrawLine(pen, run.Bounds.Left, y, run.Bounds.Right, y);
            }
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
            && _layout.Runs[index].Word)
        {
            WordClicked?.Invoke(_layout.Runs[index].Text);
        }

        base.OnMouseUp(e);
    }
}
