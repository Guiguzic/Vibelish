using System.Drawing.Drawing2D;

namespace InputTranslate;

/// <summary>
/// 卡片里的查词区：义项可以点选，选中后按这个意思改一句。
/// </summary>
sealed class WordGlossPanel : Panel
{
    readonly TextLabel _title = new() { SingleLine = true };
    readonly TextLabel _phonetic = new() { SingleLine = true };
    readonly TextLabel _banner = new();
    readonly TextLabel _note = new();
    readonly TextLabel _tip = new();
    readonly TextLabel _rewrite = new();
    readonly RoundButton _close = new();
    readonly RoundButton _rewriteButton = new();
    readonly RoundButton _adopt = new();
    readonly List<SenseChoice> _rows = new();
    WordGloss? _gloss;
    int _selected = -1;
    bool _rewriteBusy;
    bool _loading;
    bool _rewriteError;

    public WordGlossPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        TabStop = false;
        Visible = false;

        _close.Variant = ButtonVariant.Ghost;
        _close.Text = "收起";
        _rewriteButton.Text = "按这个意思改一句";
        _adopt.Text = "采用这句";
        _adopt.Variant = ButtonVariant.Primary;
        _adopt.Glyph = Glyphs.Check;
        _rewriteButton.Visible = false;
        _adopt.Visible = false;
        _close.Click += (_, _) => CloseClicked?.Invoke(this, EventArgs.Empty);
        _rewriteButton.Click += (_, _) =>
        {
            if (_rewriteBusy)
                return;
            RewriteClicked?.Invoke(this, EventArgs.Empty);
        };
        _adopt.Click += (_, _) => AdoptClicked?.Invoke(this, EventArgs.Empty);
        Controls.AddRange(new Control[] { _title, _phonetic, _banner, _note, _tip, _rewrite, _close, _rewriteButton, _adopt });
        ApplyTheme();
    }

    public string RewriteEnglish { get; private set; } = "";

    public WordSense? Selected =>
        _gloss != null && _selected >= 0 && _selected < _gloss.Senses.Count ? _gloss.Senses[_selected] : null;

    public event EventHandler? CloseClicked;
    public event EventHandler? RewriteClicked;
    public event EventHandler? AdoptClicked;
    public event EventHandler? SelectionChanged;

    public void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Surface;
        _title.Font = Theme.CardLatin(11.5F, FontStyle.Bold);
        _title.ForeColor = p.Text;
        _phonetic.Font = Theme.CardLatin(9.5F);
        _phonetic.ForeColor = p.Muted;
        _banner.Font = Theme.Card(8.5F);
        _banner.ForeColor = _loading ? p.Muted : p.Warning;
        _note.Font = Theme.Card(9.5F);
        _note.ForeColor = p.Body;
        _tip.Font = Theme.Card(8.5F);
        _tip.ForeColor = p.Muted;
        _rewrite.Font = Theme.CardLatin(10.5F, FontStyle.Bold);
        _rewrite.ForeColor = _rewriteError ? p.Error : _rewriteBusy ? p.Muted : p.Text;
        foreach (var button in new[] { _close, _rewriteButton, _adopt })
            button.Font = Theme.Card(8.5F);
        foreach (var row in _rows)
            row.Font = Theme.Card(9.5F);
        Invalidate(true);
    }

    public void Clear()
    {
        _gloss = null;
        _selected = -1;
        RewriteEnglish = "";
        _rewriteBusy = false;
        _rewriteError = false;
        _loading = false;
        _title.Text = "";
        _phonetic.Text = "";
        _banner.Text = "";
        _note.Text = "";
        _tip.Text = "";
        _rewrite.Text = "";
        _rewriteButton.Visible = false;
        _adopt.Visible = false;
        ClearRows();
        Visible = false;
    }

    public void ShowLoading(string word)
    {
        Clear();
        _loading = true;
        _title.Text = word;
        _banner.Text = "正在查释义…";
        ApplyTheme();
        Visible = true;
    }

    public void Show(WordGloss gloss)
    {
        Clear();
        _gloss = gloss;
        _selected = 0;
        for (var i = 0; i < gloss.Senses.Count; i++)
        {
            if (gloss.Senses[i].Fits)
                _selected = i;
        }

        var head = gloss.Lemma.Length == 0 || gloss.Lemma.Equals(gloss.Word, StringComparison.OrdinalIgnoreCase)
            ? (gloss.Lemma.Length == 0 ? gloss.Word : gloss.Lemma)
            : gloss.Word + " → " + gloss.Lemma;
        _title.Text = head;
        _phonetic.Text = gloss.Phonetic;
        _banner.Text = gloss.Banner;
        _tip.Text = gloss.Tip.Length == 0 ? "" : "记忆点：" + gloss.Tip;
        _note.Text = gloss.Senses.Count == 0 ? "" : gloss.Senses[_selected].Note;
        _rewriteButton.Visible = gloss.CanRewrite;
        for (var i = 0; i < gloss.Senses.Count; i++)
        {
            var index = i;
            var row = new SenseChoice(gloss.Senses[i])
            {
                Selected = i == _selected,
                Font = Theme.Card(9.5F),
            };
            row.Picked += (_, _) => Select(index);
            _rows.Add(row);
            Controls.Add(row);
        }

        ApplyTheme();
        Visible = true;
    }

    public void ShowMessage(string word, string message)
    {
        Clear();
        _title.Text = word;
        _banner.Text = message;
        ApplyTheme();
        Visible = true;
    }

    public void ShowRewriteBusy()
    {
        _rewriteBusy = true;
        _rewriteError = false;
        RewriteEnglish = "";
        _rewrite.Text = "正在按这个意思改一句…";
        _adopt.Visible = false;
        _rewriteButton.Enabled = false;
        ApplyTheme();
    }

    public void ShowRewrite(string english, string chinese)
    {
        _rewriteBusy = false;
        _rewriteError = false;
        RewriteEnglish = english;
        _rewrite.Text = chinese.Length == 0 ? english : english + "\n" + chinese;
        _adopt.Visible = english.Length > 0;
        _rewriteButton.Enabled = true;
        ApplyTheme();
    }

    public void ShowRewriteError(string message)
    {
        _rewriteBusy = false;
        _rewriteError = true;
        RewriteEnglish = "";
        _rewrite.Text = message;
        _adopt.Visible = false;
        _rewriteButton.Enabled = true;
        ApplyTheme();
    }

    public int Arrange(int width, Func<int, int> scale)
    {
        var pad = scale(12);
        var gap = scale(6);
        var inner = Math.Max(40, width - pad * 2);
        var closeWidth = _close.PreferredWidth(scale(8));
        var closeHeight = scale(24);
        _close.Visible = true;
        _close.SetBounds(Math.Max(pad, width - pad - closeWidth + scale(4)), pad - scale(2), closeWidth, closeHeight);

        var titleRoom = Math.Max(20, inner - closeWidth - scale(8));
        var titleSize = _title.Measure(titleRoom);
        var titleHeight = Math.Max(titleSize.Height, closeHeight - scale(4));
        _title.SetBounds(pad, pad, Math.Min(titleRoom, titleSize.Width + scale(2)), titleHeight);
        var y = pad + titleHeight;
        if (_phonetic.Text.Length > 0)
        {
            var phoneticSize = _phonetic.Measure(titleRoom);
            var besideX = pad + _title.Width + scale(8);
            if (besideX + phoneticSize.Width <= pad + titleRoom)
            {
                _phonetic.SetBounds(besideX, pad + (titleHeight - phoneticSize.Height) / 2 + scale(1), phoneticSize.Width + scale(2), phoneticSize.Height);
            }
            else
            {
                _phonetic.SetBounds(pad, y, titleRoom, phoneticSize.Height);
                y += phoneticSize.Height;
            }

            _phonetic.Visible = true;
        }
        else
        {
            _phonetic.Visible = false;
        }

        y = Math.Max(y, pad + closeHeight) + gap;
        y = PlaceWrapped(_banner, pad, y, inner, gap);
        var rowHeight = scale(32);
        foreach (var row in _rows)
        {
            row.SetBounds(pad, y, inner, rowHeight);
            row.Visible = true;
            y += rowHeight + scale(4);
        }

        if (_rows.Count > 0)
            y += scale(2);
        y = PlaceWrapped(_note, pad, y, inner, gap);
        y = PlaceWrapped(_tip, pad, y, inner, gap);

        var x = pad;
        var buttons = false;
        var buttonHeight = scale(28);
        if (_rewriteButton.Visible)
        {
            var buttonWidth = _rewriteButton.PreferredWidth(scale(12));
            _rewriteButton.SetBounds(x, y, buttonWidth, buttonHeight);
            x += buttonWidth + scale(8);
            buttons = true;
        }

        if (_adopt.Visible)
        {
            _adopt.SetBounds(x, y, _adopt.PreferredWidth(scale(12)), buttonHeight);
            buttons = true;
        }

        if (buttons)
            y += buttonHeight + gap;

        y = PlaceWrapped(_rewrite, pad, y, inner, scale(2));
        return y + pad - gap / 2;
    }

    void Select(int index)
    {
        if (_gloss == null || index < 0 || index >= _gloss.Senses.Count || index == _selected)
            return;
        _selected = index;
        for (var i = 0; i < _rows.Count; i++)
            _rows[i].Selected = i == index;
        _note.Text = _gloss.Senses[index].Note;
        RewriteEnglish = "";
        _rewrite.Text = "";
        _adopt.Visible = false;
        _rewriteBusy = false;
        _rewriteError = false;
        _rewriteButton.Enabled = true;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    void ClearRows()
    {
        foreach (var row in _rows)
        {
            Controls.Remove(row);
            row.Dispose();
        }

        _rows.Clear();
    }

    static int PlaceWrapped(TextLabel label, int x, int y, int width, int gap)
    {
        if (label.Text.Length == 0)
        {
            label.Visible = false;
            return y;
        }

        var height = label.Measure(width).Height;
        label.Visible = true;
        label.SetBounds(x, y, width, height);
        return y + height + gap;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(bounds, Theme.Scale(this, 10));
        using var fill = new SolidBrush(p.Surface);
        g.FillPath(fill, path);
        using var pen = new Pen(p.SurfaceBorder);
        g.DrawPath(pen, path);
    }
}

sealed class SenseChoice : Control
{
    bool _selected;
    bool _hover;

    public SenseChoice(WordSense sense)
    {
        Sense = sense;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        Cursor = Cursors.Hand;
        TabStop = false;
        Height = 32;
    }

    public WordSense Sense { get; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            Invalidate();
        }
    }

    public event EventHandler? Picked;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Current.Surface);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (var path = Theme.RoundedRect(bounds, Theme.Scale(this, 8)))
        {
            using var fill = new SolidBrush(Selected ? p.Selected : _hover ? p.ButtonHover : p.Card);
            g.FillPath(fill, path);
            using var pen = new Pen(Selected ? p.Accent : p.ButtonBorder);
            g.DrawPath(pen, path);
        }

        var pad = Theme.Scale(this, 10);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var x = pad;
        if (Sense.Pos.Length > 0)
        {
            var posFont = Theme.CardLatin(Font.SizeInPoints / Theme.TextScale * 0.92f, FontStyle.Italic);
            var posWidth = TextRenderer.MeasureText(Sense.Pos, posFont, new Size(int.MaxValue, Height), flags | TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, Sense.Pos, posFont, new Rectangle(x, 0, posWidth + 2, Height), p.Muted, flags | TextFormatFlags.NoPadding);
            x += posWidth + Theme.Scale(this, 8);
        }

        var badgeWidth = 0;
        if (Sense.Fits)
        {
            var badgeFont = Theme.Card(Font.SizeInPoints / Theme.TextScale * 0.82f, FontStyle.Bold);
            var size = TextRenderer.MeasureText("本句", badgeFont, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding);
            var bw = size.Width + Theme.Scale(this, 12);
            var bh = size.Height + Theme.Scale(this, 4);
            var badge = new Rectangle(Width - pad - bw, (Height - bh) / 2, bw, bh);
            using (var path = Theme.RoundedRect(badge, bh / 2))
            using (var fill = new SolidBrush(p.AccentSoft))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, "本句", badgeFont, badge, p.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            badgeWidth = bw + Theme.Scale(this, 8);
        }

        TextRenderer.DrawText(
            g,
            Sense.Chinese,
            Font,
            new Rectangle(x, 0, Math.Max(10, Width - x - pad - badgeWidth), Height),
            p.Text,
            flags | TextFormatFlags.EndEllipsis);
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
        Picked?.Invoke(this, EventArgs.Empty);
        base.OnClick(e);
    }
}
