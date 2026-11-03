using System.Drawing.Drawing2D;

namespace Vibelish;

/// <summary>
/// 卡片里的查词区：浅灰圆角底，义项是一列可点的行，选中的打勾；选中后可以按这个意思改一句。
/// </summary>
sealed class WordGlossPanel : Panel
{
    readonly TextLabel _title = new() { SingleLine = true };
    readonly TextLabel _phonetic = new() { SingleLine = true };
    readonly TextLabel _banner = new();
    readonly TextLabel _note = new();
    readonly TextLabel _tipLabel = new() { SingleLine = true };
    readonly TextLabel _tip = new();
    readonly TextLabel _rewrite = new();
    readonly RoundButton _speak = new();
    readonly RoundButton _close = new();
    readonly RoundButton _rewriteButton = new();
    readonly RoundButton _adopt = new();
    readonly List<SenseChoice> _rows = new();
    readonly List<int> _separators = new();
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
        _close.CornerRadius = 99;
        _speak.Variant = ButtonVariant.Ghost;
        _speak.Glyph = Glyphs.Speaker;
        _speak.CornerRadius = 99;
        _rewriteButton.Text = "按这个意思改一句";
        _rewriteButton.Variant = ButtonVariant.Link;
        _rewriteButton.CornerRadius = 99;
        _adopt.Text = "采用这句";
        _adopt.Variant = ButtonVariant.Primary;
        _adopt.Glyph = Glyphs.Check;
        _adopt.CornerRadius = 99;
        _tipLabel.Text = "记忆点";
        _rewriteButton.Visible = false;
        _adopt.Visible = false;
        _speak.Visible = Theme.Icons(9) != null;
        _close.Click += (_, _) => CloseClicked?.Invoke(this, EventArgs.Empty);
        _speak.Click += (_, _) => SpeakClicked?.Invoke(this, EventArgs.Empty);
        _rewriteButton.Click += (_, _) =>
        {
            if (_rewriteBusy)
                return;
            RewriteClicked?.Invoke(this, EventArgs.Empty);
        };
        _adopt.Click += (_, _) => AdoptClicked?.Invoke(this, EventArgs.Empty);
        Controls.AddRange(new Control[] { _title, _phonetic, _speak, _banner, _note, _tipLabel, _tip, _rewrite, _close, _rewriteButton, _adopt });
        ApplyTheme();
    }

    public string RewriteEnglish { get; private set; } = "";

    public WordSense? Selected =>
        _gloss != null && _selected >= 0 && _selected < _gloss.Senses.Count ? _gloss.Senses[_selected] : null;

    public event EventHandler? CloseClicked;
    public event EventHandler? SpeakClicked;
    public event EventHandler? RewriteClicked;
    public event EventHandler? AdoptClicked;
    public event EventHandler? SelectionChanged;

    public void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Surface;
        _title.Font = Theme.CardLatin(12.5F, FontStyle.Bold);
        _title.ForeColor = p.Text;
        _phonetic.Font = Theme.CardLatin(9.5F);
        _phonetic.ForeColor = p.Muted;
        _banner.Font = Theme.Card(8.75F);
        _banner.ForeColor = _loading ? p.Muted : p.Warning;
        _note.Font = Theme.Card(9.25F);
        _note.ForeColor = p.Body;
        _tipLabel.Font = Theme.Card(8.25F, FontStyle.Bold);
        _tipLabel.ForeColor = p.Muted;
        _tip.Font = Theme.Card(8.75F);
        _tip.ForeColor = p.Body;
        _rewrite.Font = Theme.CardLatin(10.5F, FontStyle.Bold);
        _rewrite.ForeColor = _rewriteError ? p.Error : _rewriteBusy ? p.Muted : p.Text;
        foreach (var button in new[] { _close, _rewriteButton, _adopt })
            button.Font = Theme.Card(8.75F);
        _speak.Font = Theme.Card(9F);
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
        _tip.Text = gloss.Tip;
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
        var pad = scale(14);
        var gap = scale(8);
        var inner = Math.Max(40, width - pad * 2);
        _separators.Clear();

        var closeWidth = _close.PreferredWidth(scale(10));
        var buttonHeight = scale(26);
        _close.Visible = true;
        _close.SetBounds(width - pad - closeWidth + scale(6), pad - scale(4), closeWidth, buttonHeight);

        var titleRoom = Math.Max(20, inner - closeWidth - scale(8));
        var titleSize = _title.Measure(titleRoom);
        var titleHeight = Math.Max(titleSize.Height, buttonHeight - scale(6));
        _title.SetBounds(pad, pad - scale(1), Math.Min(titleRoom, titleSize.Width + scale(2)), titleHeight);
        var x = _title.Right + scale(8);
        if (_phonetic.Text.Length > 0)
        {
            var phoneticSize = _phonetic.Measure(titleRoom);
            _phonetic.Visible = x + phoneticSize.Width <= pad + titleRoom;
            if (_phonetic.Visible)
            {
                _phonetic.SetBounds(x, pad - scale(1) + (titleHeight - phoneticSize.Height) / 2 + scale(1), phoneticSize.Width + scale(2), phoneticSize.Height);
                x = _phonetic.Right + scale(4);
            }
        }
        else
        {
            _phonetic.Visible = false;
        }

        var speakSize = scale(24);
        _speak.Visible = _speak.Glyph.Length > 0 && Theme.Icons(9) != null && !_loading && _title.Text.Length > 0
            && x + speakSize <= pad + titleRoom;
        if (_speak.Visible)
            _speak.SetBounds(x, pad - scale(1) + (titleHeight - speakSize) / 2, speakSize, speakSize);

        var y = pad + Math.Max(titleHeight, buttonHeight - scale(4)) + scale(6);
        y = PlaceWrapped(_banner, pad, y, inner, gap);

        if (_rows.Count > 0)
        {
            var rowHeight = scale(34);
            _separators.Add(y);
            foreach (var row in _rows)
            {
                row.SetBounds(pad - scale(6), y, inner + scale(12), rowHeight);
                row.Visible = true;
                y += rowHeight;
                _separators.Add(y);
            }

            y += scale(10);
        }

        y = PlaceWrapped(_note, pad, y, inner, gap);
        if (_tip.Text.Length > 0)
        {
            var labelWidth = _tipLabel.Measure(inner).Width;
            _tipLabel.Visible = true;
            var tipHeight = _tip.Measure(inner - labelWidth - scale(8)).Height;
            _tipLabel.SetBounds(pad, y + scale(1), labelWidth, Math.Min(tipHeight, _tip.Font.Height + scale(2)));
            _tip.Visible = true;
            _tip.SetBounds(pad + labelWidth + scale(8), y, inner - labelWidth - scale(8), tipHeight);
            y += tipHeight + gap;
        }
        else
        {
            _tipLabel.Visible = false;
            _tip.Visible = false;
        }

        var bx = pad - scale(8);
        var buttons = false;
        if (_rewriteButton.Visible)
        {
            var w = _rewriteButton.PreferredWidth(scale(8));
            _rewriteButton.SetBounds(bx, y, w, buttonHeight);
            bx += w + scale(6);
            buttons = true;
        }

        if (_adopt.Visible)
        {
            _adopt.SetBounds(bx, y, _adopt.PreferredWidth(scale(12)), buttonHeight);
            buttons = true;
        }

        if (buttons)
            y += buttonHeight + scale(4);

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
        _separators.Clear();
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
        using (var path = Theme.RoundedRect(bounds, Theme.Scale(this, 12)))
        using (var fill = new SolidBrush(p.Surface))
            g.FillPath(fill, path);

        if (_separators.Count > 1)
        {
            using var pen = new Pen(p.Dark ? p.Separator : p.SurfaceBorder);
            var pad = Theme.Scale(this, 14);
            for (var i = 1; i < _separators.Count - 1; i++)
                g.DrawLine(pen, pad, _separators[i], Width - pad, _separators[i]);
        }
    }
}

/// <summary>一个义项：词性 + 中文；合这句的标「本句」，选中的打勾。</summary>
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
        Height = 34;
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
        e.Graphics.Clear(Theme.Current.Surface);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hover)
        {
            var inset = Theme.Scale(this, 2);
            using var path = Theme.RoundedRect(new Rectangle(0, inset, Width - 1, Height - inset * 2 - 1), Theme.Scale(this, 7));
            using var fill = new SolidBrush(p.Dark ? p.FillHover : Theme.Mix(p.Surface, Color.Black, 0.04));
            g.FillPath(fill, path);
        }

        var pad = Theme.Scale(this, 6);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var x = pad;
        var posFont = Theme.CardLatin(Font.SizeInPoints / Theme.TextScale * 0.9f, FontStyle.Italic);
        var posWidth = Theme.Scale(this, 30);
        if (Sense.Pos.Length > 0)
            TextRenderer.DrawText(g, Sense.Pos, posFont, new Rectangle(x, 0, posWidth, Height), p.Muted, flags | TextFormatFlags.EndEllipsis);
        x += posWidth;

        var right = Width - pad;
        if (Selected && Theme.Icons(9) is { } icons)
        {
            var size = Theme.Scale(this, 16);
            TextRenderer.DrawText(g, Glyphs.Check, icons, new Rectangle(right - size, 0, size, Height), p.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            right -= size + Theme.Scale(this, 6);
        }

        if (Sense.Fits)
        {
            var badgeFont = Theme.Card(Font.SizeInPoints / Theme.TextScale * 0.8f, FontStyle.Bold);
            var size = TextRenderer.MeasureText("本句", badgeFont, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding);
            var bw = size.Width + Theme.Scale(this, 12);
            var bh = size.Height + Theme.Scale(this, 4);
            var badge = new Rectangle(right - bw, (Height - bh) / 2, bw, bh);
            using (var path = Theme.RoundedRect(badge, bh / 2))
            using (var fill = new SolidBrush(p.AccentSoft))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, "本句", badgeFont, badge, p.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            right = badge.Left - Theme.Scale(this, 8);
        }

        TextRenderer.DrawText(g, Sense.Chinese, Font, new Rectangle(x, 0, Math.Max(10, right - x), Height),
            Selected ? p.Accent : p.Text, flags | TextFormatFlags.EndEllipsis);
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
