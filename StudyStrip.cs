using System.Drawing.Drawing2D;

namespace InputTranslate;

/// <summary>
/// 译文出来后列出这句里值得记的词。三种样式：只有单词、单词 + 中文释义（默认）、
/// 详细（每个词一行，再加一句为什么值得记）。点一下看完整释义。
/// </summary>
sealed class StudyStrip : Control
{
    readonly List<StudyWord> _words = new();
    readonly List<Rectangle> _boxes = new();
    readonly ToolTip _tips = new() { ShowAlways = true, InitialDelay = 300, AutoPopDelay = 8000 };
    string _status = "";
    int _hover = -1;
    int _down = -1;
    int _tipFor = -1;
    int _wordColumn;
    int _meaningColumn;

    public StudyStrip()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        TabStop = false;
        Visible = false;
        ApplyTheme();
    }

    public bool Open { get; private set; }

    /// <summary>word、meaning 或 detail。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Style { get; set; } = "meaning";

    public event Action<string>? WordClicked;

    Font WordFont => Theme.CardLatin(9.5F, FontStyle.Bold);

    Font MeaningFont => Theme.Card(9F);

    Font NoteFont => Theme.Card(8.5F);

    public void ApplyTheme()
    {
        BackColor = Theme.Current.Card;
        Invalidate();
    }

    public void Clear()
    {
        Open = false;
        _status = "";
        _words.Clear();
        _boxes.Clear();
        _hover = -1;
        _tips.SetToolTip(this, null);
        Visible = false;
    }

    public void ShowLoading()
    {
        _words.Clear();
        _boxes.Clear();
        _status = "正在挑这句里值得记的词…";
        Open = true;
        Visible = true;
        Invalidate();
    }

    public void ShowWords(IReadOnlyList<StudyWord> words)
    {
        _words.Clear();
        foreach (var word in words)
        {
            if (word.English.Trim().Length > 0)
                _words.Add(word);
        }

        _status = "";
        if (_words.Count == 0)
        {
            Clear();
            return;
        }

        Open = true;
        Visible = true;
        Invalidate();
    }

    static int TextWidth(string text, Font font) => text.Length == 0 ? 0
        : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;

    bool ShowMeaning => Style != "word";

    /// <summary>按宽度排好每个词，返回高度。</summary>
    public int Arrange(int width, Func<int, int> scale)
    {
        _boxes.Clear();
        if (_words.Count == 0)
            return scale(20);

        if (Style == "detail")
        {
            // 三列对齐：单词、释义、说明，像一张小表。
            _wordColumn = 0;
            _meaningColumn = 0;
            foreach (var word in _words)
            {
                _wordColumn = Math.Max(_wordColumn, TextWidth(word.English.Trim(), WordFont));
                _meaningColumn = Math.Max(_meaningColumn, TextWidth(word.Chinese, MeaningFont));
            }

            _wordColumn = Math.Min(_wordColumn + scale(14), width * 2 / 5);
            _meaningColumn = Math.Min(_meaningColumn + scale(14), width * 3 / 10);
            var rowHeight = Math.Max(WordFont.Height, MeaningFont.Height) + scale(8);
            var y = 0;
            foreach (var _ in _words)
            {
                _boxes.Add(new Rectangle(0, y, width, rowHeight));
                y += rowHeight + scale(2);
            }

            return y - scale(2);
        }

        var chipHeight = Math.Max(WordFont.Height, MeaningFont.Height) + scale(10);
        var gap = scale(6);
        var padX = scale(11);
        var x = 0;
        var top = 0;
        foreach (var word in _words)
        {
            var chipWidth = padX * 2 + TextWidth(word.English.Trim(), WordFont);
            if (ShowMeaning && word.Chinese.Length > 0)
                chipWidth += scale(6) + TextWidth(word.Chinese, MeaningFont);
            chipWidth = Math.Min(width, chipWidth);
            if (x > 0 && x + chipWidth > width)
            {
                x = 0;
                top += chipHeight + gap;
            }

            _boxes.Add(new Rectangle(x, top, chipWidth, chipHeight));
            x += chipWidth + gap;
        }

        return top + chipHeight;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Current.Card);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        if (_words.Count == 0)
        {
            TextRenderer.DrawText(g, _status, NoteFont, ClientRectangle, p.Dim, flags | TextFormatFlags.EndEllipsis);
            return;
        }

        var pad = Theme.Scale(this, 11);
        for (var i = 0; i < _words.Count && i < _boxes.Count; i++)
        {
            var word = _words[i];
            var box = _boxes[i];
            var english = word.English.Trim();
            if (Style == "detail")
            {
                if (i == _hover)
                {
                    using var path = Theme.RoundedRect(new Rectangle(box.X - Theme.Scale(this, 6), box.Y, box.Width + Theme.Scale(this, 6), box.Height), Theme.Scale(this, 6));
                    using var fill = new SolidBrush(p.Fill);
                    g.FillPath(fill, path);
                }

                var x = box.X;
                TextRenderer.DrawText(g, english, WordFont, new Rectangle(x, box.Y, _wordColumn - Theme.Scale(this, 8), box.Height), p.Accent, flags | TextFormatFlags.EndEllipsis);
                x += _wordColumn;
                TextRenderer.DrawText(g, word.Chinese, MeaningFont, new Rectangle(x, box.Y, _meaningColumn - Theme.Scale(this, 8), box.Height), p.Body, flags | TextFormatFlags.EndEllipsis);
                x += _meaningColumn;
                if (word.Why.Length > 0 && x < box.Right - Theme.Scale(this, 30))
                    TextRenderer.DrawText(g, word.Why, NoteFont, new Rectangle(x, box.Y, box.Right - x, box.Height), p.Muted, flags | TextFormatFlags.EndEllipsis);
                continue;
            }

            using (var path = Theme.RoundedRect(box, box.Height / 2))
            using (var fill = new SolidBrush(i == _down ? p.ButtonPressed : i == _hover ? p.FillHover : p.Fill))
                g.FillPath(fill, path);

            var left = box.X + pad;
            var ew = TextWidth(english, WordFont);
            TextRenderer.DrawText(g, english, WordFont, new Rectangle(left, box.Y, Math.Min(ew + 2, box.Right - left), box.Height), p.Text, flags | TextFormatFlags.EndEllipsis);
            if (ShowMeaning && word.Chinese.Length > 0)
            {
                var mx = left + ew + Theme.Scale(this, 6);
                if (mx < box.Right - pad)
                    TextRenderer.DrawText(g, word.Chinese, MeaningFont, new Rectangle(mx, box.Y, box.Right - pad - mx + 2, box.Height), p.Muted, flags | TextFormatFlags.EndEllipsis);
            }
        }
    }

    int IndexAt(Point point)
    {
        for (var i = 0; i < _boxes.Count; i++)
        {
            if (_boxes[i].Contains(point))
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
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        if (index != _tipFor)
        {
            _tipFor = index;
            var why = index >= 0 && index < _words.Count && Style != "detail" ? _words[index].Why : "";
            _tips.SetToolTip(this, why.Length > 0 ? why : null);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        _down = -1;
        _tipFor = -1;
        Cursor = Cursors.Default;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _down = e.Button == MouseButtons.Left ? IndexAt(e.Location) : -1;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var index = _down;
        _down = -1;
        Invalidate();
        if (e.Button == MouseButtons.Left && index >= 0 && index == IndexAt(e.Location) && index < _words.Count)
            WordClicked?.Invoke(_words[index].English.Trim());
        base.OnMouseUp(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();
        base.Dispose(disposing);
    }
}
