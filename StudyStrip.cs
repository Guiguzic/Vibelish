namespace InputTranslate;

/// <summary>
/// 译文出来后自动列出这句里值得记的词，点一下再看详细释义。
/// </summary>
sealed class StudyStrip : Panel
{
    readonly Label _status = new();
    readonly List<RoundButton> _chips = new();

    public StudyStrip()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(17, 25, 40);
        TabStop = false;
        Visible = false;
        _status.AutoSize = false;
        _status.BackColor = Color.Transparent;
        _status.ForeColor = Theme.Accent;
        _status.Font = new Font("Microsoft YaHei UI", 8.5F);
        _status.UseMnemonic = false;
        Controls.Add(_status);
    }

    public bool Open { get; private set; }

    public event Action<string>? WordClicked;

    public void Clear()
    {
        Open = false;
        _status.Text = "";
        ClearChips();
        Visible = false;
    }

    public void ShowLoading()
    {
        ClearChips();
        _status.Text = "正在挑这句里值得记的词…";
        Open = true;
        Visible = true;
    }

    public void ShowWords(IReadOnlyList<StudyWord> words)
    {
        ClearChips();
        if (words.Count == 0)
        {
            Clear();
            return;
        }

        _status.Text = "这句里";
        foreach (var word in words)
        {
            var english = word.English.Trim();
            if (english.Length == 0)
                continue;
            var chip = new RoundButton
            {
                Text = word.Chinese.Length == 0 ? english : english + "  " + word.Chinese,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                Tag = english,
                Height = 28,
            };
            chip.Click += (_, _) =>
            {
                if (chip.Tag is string text && text.Length > 0)
                    WordClicked?.Invoke(text);
            };
            _chips.Add(chip);
            Controls.Add(chip);
        }

        if (_chips.Count == 0)
        {
            Clear();
            return;
        }

        Open = true;
        Visible = true;
    }

    public int Arrange(int width, Func<int, int> scale)
    {
        var chipHeight = scale(28);
        var gap = scale(6);
        if (_chips.Count == 0)
        {
            var height = scale(22);
            _status.SetBounds(0, 0, width, height);
            _status.Visible = _status.Text.Length > 0;
            return height;
        }

        var x = 0;
        var y = 0;
        var statusWidth = TextWidth(_status) + scale(8);
        _status.Visible = true;
        _status.SetBounds(0, scale(4), statusWidth, chipHeight);
        x = statusWidth;
        foreach (var chip in _chips)
        {
            var chipWidth = TextWidth(chip) + scale(16);
            if (x > statusWidth && x + chipWidth > width)
            {
                x = 0;
                y += chipHeight + gap;
            }

            chip.SetBounds(x, y, chipWidth, chipHeight);
            chip.Visible = true;
            x += chipWidth + gap;
        }

        return y + chipHeight;
    }

    void ClearChips()
    {
        foreach (var chip in _chips)
        {
            Controls.Remove(chip);
            chip.Dispose();
        }

        _chips.Clear();
    }

    static int TextWidth(Control control)
    {
        return TextRenderer.MeasureText(
            control.Text.Length == 0 ? " " : control.Text,
            control.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }
}
