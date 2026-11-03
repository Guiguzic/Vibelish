namespace InputTranslate;

/// <summary>
/// 译文出来后自动列出这句里值得记的词，点一下再看详细释义；鼠标停在词上显示为什么值得记。
/// </summary>
sealed class StudyStrip : Panel
{
    readonly TextLabel _status = new() { SingleLine = true };
    readonly List<RoundButton> _chips = new();
    readonly ToolTip _tips = new() { ShowAlways = true, InitialDelay = 300, AutoPopDelay = 8000 };

    public StudyStrip()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        TabStop = false;
        Visible = false;
        Controls.Add(_status);
        ApplyTheme();
    }

    public bool Open { get; private set; }

    public event Action<string>? WordClicked;

    public void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Card;
        _status.ForeColor = p.Muted;
        _status.Font = Theme.Card(8.5F);
        foreach (var chip in _chips)
            chip.Font = Theme.Card(8.5F);
        Invalidate(true);
    }

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
        _status.Text = "重点词";
        foreach (var word in words)
        {
            var english = word.English.Trim();
            if (english.Length == 0)
                continue;
            var chip = new RoundButton
            {
                Text = word.Chinese.Length == 0 ? english : english + "  " + word.Chinese,
                Font = Theme.Card(8.5F),
                Tag = english,
                CornerRadius = 12,
            };
            chip.Click += (_, _) =>
            {
                if (chip.Tag is string text && text.Length > 0)
                    WordClicked?.Invoke(text);
            };
            if (word.Why.Length > 0)
                _tips.SetToolTip(chip, word.Why);
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
        var chipHeight = scale(26);
        var gap = scale(6);
        if (_chips.Count == 0)
        {
            var height = scale(22);
            _status.SetBounds(0, 0, width, height);
            _status.Visible = _status.Text.Length > 0;
            return height;
        }

        var statusWidth = _status.Measure(width).Width + scale(6);
        _status.Visible = true;
        _status.SetBounds(0, 0, statusWidth, chipHeight);
        var x = statusWidth;
        var y = 0;
        foreach (var chip in _chips)
        {
            var chipWidth = Math.Min(width, chip.PreferredWidth(scale(10)));
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
            _tips.SetToolTip(chip, null);
            Controls.Remove(chip);
            chip.Dispose();
        }

        _chips.Clear();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();
        base.Dispose(disposing);
    }
}
