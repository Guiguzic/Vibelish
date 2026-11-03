namespace InputTranslate;

/// <summary>
/// 卡片里的查词区：义项可以点选，选中后按这个意思改一句。
/// </summary>
sealed class WordGlossPanel : Panel
{
    readonly Label _title = new();
    readonly Label _banner = new();
    readonly Label _note = new();
    readonly Label _tip = new();
    readonly Label _rewrite = new();
    readonly RoundButton _close = new();
    readonly RoundButton _rewriteButton = new();
    readonly RoundButton _adopt = new();
    readonly List<SenseChoice> _rows = new();
    WordGloss? _gloss;
    int _selected = -1;
    bool _rewriteBusy;

    public WordGlossPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(20, 32, 54);
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        TabStop = false;
        Visible = false;

        SetupLabel(_title, new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold), Color.White);
        SetupLabel(_banner, new Font("Microsoft YaHei UI", 8.5F), Theme.Muted);
        SetupLabel(_note, new Font("Microsoft YaHei UI", 9.5F), Theme.Body);
        SetupLabel(_tip, new Font("Microsoft YaHei UI", 8.5F), Theme.Muted);
        SetupLabel(_rewrite, new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold), Color.White);

        StyleMini(_close, "收起");
        StyleMini(_rewriteButton, "按这个意思改一句");
        StyleMini(_adopt, "采用这句");
        _close.Click += (_, _) => CloseClicked?.Invoke(this, EventArgs.Empty);
        _rewriteButton.Click += (_, _) =>
        {
            if (_rewriteBusy)
                return;
            RewriteClicked?.Invoke(this, EventArgs.Empty);
        };
        _adopt.Click += (_, _) => AdoptClicked?.Invoke(this, EventArgs.Empty);
        Controls.AddRange(new Control[] { _title, _banner, _note, _tip, _rewrite, _close, _rewriteButton, _adopt });
    }

    public string RewriteEnglish { get; private set; } = "";

    public WordSense? Selected =>
        _gloss != null && _selected >= 0 && _selected < _gloss.Senses.Count ? _gloss.Senses[_selected] : null;

    public event EventHandler? CloseClicked;
    public event EventHandler? RewriteClicked;
    public event EventHandler? AdoptClicked;
    public event EventHandler? SelectionChanged;

    public void Clear()
    {
        _gloss = null;
        _selected = -1;
        RewriteEnglish = "";
        _rewriteBusy = false;
        _title.Text = "";
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
        _title.Text = word + "   ·   正在查释义";
        _close.Visible = true;
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

        _title.Text = FormatTitle(gloss);
        _banner.Text = gloss.Banner;
        _tip.Text = gloss.Tip.Length == 0 ? "" : "记：" + gloss.Tip;
        _note.Text = gloss.Senses.Count == 0 ? "" : gloss.Senses[_selected].Note;
        _rewriteButton.Visible = gloss.CanRewrite;
        _close.Visible = true;
        for (var i = 0; i < gloss.Senses.Count; i++)
        {
            var index = i;
            var row = new SenseChoice(gloss.Senses[i])
            {
                Selected = i == _selected,
                Font = Font,
            };
            row.Picked += (_, _) => Select(index);
            _rows.Add(row);
            Controls.Add(row);
        }

        Visible = true;
    }

    public void ShowMessage(string word, string message)
    {
        Clear();
        _title.Text = word;
        _banner.Text = message;
        _close.Visible = true;
        Visible = true;
    }

    public void ShowRewriteBusy()
    {
        _rewriteBusy = true;
        RewriteEnglish = "";
        _rewrite.ForeColor = Theme.Muted;
        _rewrite.Text = "正在按这个意思改一句…";
        _adopt.Visible = false;
    }

    public void ShowRewrite(string english, string chinese)
    {
        _rewriteBusy = false;
        RewriteEnglish = english;
        _rewrite.ForeColor = Color.White;
        _rewrite.Text = chinese.Length == 0 ? english : english + "\n" + chinese;
        _adopt.Visible = english.Length > 0;
    }

    public void ShowRewriteError(string message)
    {
        _rewriteBusy = false;
        RewriteEnglish = "";
        _rewrite.ForeColor = Theme.Error;
        _rewrite.Text = message;
        _adopt.Visible = false;
    }

    public int Arrange(int width, Func<int, int> scale)
    {
        var pad = scale(10);
        var gap = scale(6);
        var inner = Math.Max(40, width - pad * 2);
        var closeWidth = TextWidth(_close) + scale(16);
        var closeHeight = scale(26);
        _close.Visible = true;
        _close.SetBounds(Math.Max(pad, width - pad - closeWidth), pad, closeWidth, closeHeight);

        var titleWidth = Math.Max(20, inner - closeWidth - scale(8));
        var titleHeight = TextHeight(_title.Text, _title.Font, titleWidth);
        _title.SetBounds(pad, pad, titleWidth, titleHeight);
        var y = pad + Math.Max(titleHeight, closeHeight) + gap;

        y = PlaceWrapped(_banner, pad, y, inner, gap);
        foreach (var row in _rows)
        {
            row.SetBounds(pad, y, inner, scale(34));
            row.Visible = true;
            y += scale(34) + scale(4);
        }

        y = PlaceWrapped(_note, pad, y, inner, gap);
        y = PlaceWrapped(_tip, pad, y, inner, gap);

        var x = pad;
        var buttons = false;
        if (_rewriteButton.Visible)
        {
            var buttonWidth = TextWidth(_rewriteButton) + scale(18);
            _rewriteButton.SetBounds(x, y, buttonWidth, scale(30));
            x += buttonWidth + scale(8);
            buttons = true;
        }

        if (_adopt.Visible)
        {
            _adopt.SetBounds(x, y, TextWidth(_adopt) + scale(18), scale(30));
            buttons = true;
        }

        if (buttons)
            y += scale(30) + gap;

        y = PlaceWrapped(_rewrite, pad, y, inner, scale(2));
        return y + pad;
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

    static string FormatTitle(WordGloss gloss)
    {
        var head = gloss.Lemma.Length == 0 || gloss.Lemma.Equals(gloss.Word, StringComparison.OrdinalIgnoreCase)
            ? (gloss.Lemma.Length == 0 ? gloss.Word : gloss.Lemma)
            : gloss.Word + "  →  " + gloss.Lemma;
        if (gloss.Phonetic.Length > 0)
            head += "    " + gloss.Phonetic;
        return head;
    }

    static void SetupLabel(Label label, Font font, Color color)
    {
        label.AutoSize = false;
        label.BackColor = Color.Transparent;
        label.ForeColor = color;
        label.Font = font;
        label.UseMnemonic = false;
    }

    static void StyleMini(RoundButton button, string text)
    {
        button.Text = text;
        button.Font = new Font("Microsoft YaHei UI", 8.5F);
        button.CornerRadius = 7;
        button.Visible = false;
        button.Height = 26;
    }

    static int PlaceWrapped(Label label, int x, int y, int width, int gap)
    {
        if (label.Text.Length == 0)
        {
            label.Visible = false;
            return y;
        }

        var height = TextHeight(label.Text, label.Font, width);
        label.Visible = true;
        label.SetBounds(x, y, width, height);
        return y + height + gap;
    }

    static int TextHeight(string text, Font font, int width)
    {
        var size = TextRenderer.MeasureText(
            text.Length == 0 ? " " : text,
            font,
            new Size(Math.Max(10, width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return Math.Max(size.Height, font.Height + 2);
    }

    static int TextWidth(Control control)
    {
        return TextRenderer.MeasureText(
            control.Text,
            control.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }
}

sealed class SenseChoice : Control
{
    public SenseChoice(WordSense sense)
    {
        Sense = sense;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        TabStop = false;
        Height = 34;
    }

    public WordSense Sense { get; }

    bool _selected;

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
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = Theme.RoundedRect(bounds, 8);
        using var fill = new SolidBrush(Selected ? Color.FromArgb(30, 58, 90) : Color.FromArgb(15, 23, 42));
        g.FillPath(fill, path);
        using var pen = new Pen(Selected ? Theme.Accent : Color.FromArgb(51, 65, 85));
        g.DrawPath(pen, path);

        var badge = Sense.Fits ? 48 : 8;
        var text = (Sense.Pos.Length > 0 ? Sense.Pos + "   " : "") + Sense.Chinese;
        TextRenderer.DrawText(
            g,
            text,
            Font,
            new Rectangle(10, 0, Math.Max(10, Width - 16 - badge), Height),
            Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Sense.Fits)
        {
            TextRenderer.DrawText(
                g,
                "本句",
                Font,
                new Rectangle(Width - 54, 0, 44, Height),
                Theme.Accent,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        Picked?.Invoke(this, EventArgs.Empty);
        base.OnClick(e);
    }
}
