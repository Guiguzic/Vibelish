using System.Drawing.Drawing2D;

namespace InputTranslate;

sealed class OverlayForm : Form
{
    const int WsExNoActivate = 0x08000000;
    const int CsDropShadow = 0x00020000;

    readonly Label _badge = new();
    readonly Label _source = new();
    readonly ClickableEnglish _english = new();
    readonly Label _hint = new();
    readonly StudyStrip _study = new();
    readonly WordGlossPanel _gloss = new();
    readonly Label _status = new();
    readonly RoundButton _copy = new();
    readonly RoundButton _write = new();
    readonly RoundButton _close = new();
    bool _dragPending;
    bool _drag;
    Point _dragOrigin;
    Point _dragPress;
    bool _movedByUser;
    bool _anchoredAbove;
    string _badgeText = "";
    string _glossWord = "";
    bool _glossOpen;
    bool _studyOpen;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Text = "输入翻译";
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Background;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;

        _badge.AutoSize = false;
        _badge.TextAlign = ContentAlignment.MiddleCenter;
        _badge.Font = new Font("Microsoft YaHei UI", 7.5F, FontStyle.Bold);
        _badge.BackColor = Theme.BadgeBack;
        _badge.ForeColor = Theme.BadgeText;

        _source.AutoSize = false;
        _source.BackColor = Color.Transparent;
        _source.ForeColor = Theme.Body;
        _source.Font = new Font("Microsoft YaHei UI", 11F);

        _english.Font = new Font("Microsoft YaHei UI", 15.5F, FontStyle.Bold);
        _english.ForeColor = Color.White;
        _english.SuppressClick = () => _drag;
        _english.WordClicked += word => WordClicked?.Invoke(word);

        _hint.AutoSize = false;
        _hint.BackColor = Color.Transparent;
        _hint.ForeColor = Theme.Dim;
        _hint.Font = new Font("Microsoft YaHei UI", 8.5F);
        _hint.Text = "点单词查释义";
        _hint.UseMnemonic = false;

        _study.WordClicked += word => WordClicked?.Invoke(word);
        _gloss.CloseClicked += (_, _) => CloseGloss();
        _gloss.RewriteClicked += (_, _) => RewriteRequested?.Invoke(this, EventArgs.Empty);
        _gloss.AdoptClicked += (_, _) => AdoptRequested?.Invoke(this, EventArgs.Empty);
        _gloss.SelectionChanged += (_, _) => Relayout();

        _status.AutoSize = false;
        _status.BackColor = Color.Transparent;
        _status.ForeColor = Theme.Muted;
        _status.Font = new Font("Microsoft YaHei UI", 8.5F);

        StyleButton(_copy, "复制", 76);
        StyleButton(_write, "写入输入框", 116);
        _close.Text = "×";
        _close.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        _close.CornerRadius = 6;
        _close.NormalBack = Color.FromArgb(17, 26, 43);
        _close.HoverBack = Color.FromArgb(127, 29, 29);
        _close.PressedBack = Color.FromArgb(20, 30, 46);
        _close.BorderColor = Color.FromArgb(30, 41, 59);
        _close.HoverBorderColor = Color.FromArgb(248, 113, 113);
        _close.TextColor = Theme.Muted;
        _close.Click += (_, _) => Hide();

        _copy.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);
        _write.Click += (_, _) => WriteRequested?.Invoke(this, EventArgs.Empty);

        Controls.AddRange(new Control[] { _badge, _source, _english, _study, _hint, _gloss, _status, _copy, _write, _close });
        AttachDrag(this);
        AttachDrag(_source);
        AttachDrag(_english);
        AttachDrag(_hint);
        AttachDrag(_badge);
    }

    public string SourceText { get; private set; } = "";
    public string EnglishText { get; private set; } = "";

    public event EventHandler? CopyRequested;
    public event EventHandler? WriteRequested;
    public event Action<string>? WordClicked;
    public event EventHandler? RewriteRequested;
    public event EventHandler? AdoptRequested;

    public bool GlossOpen => _glossOpen;
    public string GlossWord => _glossWord;
    public WordSense? SelectedSense => _glossOpen ? _gloss.Selected : null;
    public string RewriteEnglish => _gloss.RewriteEnglish;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The tray app keeps this instance forever; closing the card only hides it.
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutCard();
    }

    public enum CardPlace
    {
        Stay,
        Cursor,
        TopCenter,
        AboveInput,
    }

    public void SetBadge(string text)
    {
        var next = text ?? "";
        if (_badgeText == next)
            return;
        _badgeText = next;
        if (Visible)
            LayoutCard();
    }

    public void ShowSession(string chinese, string english, string hint, CardPlace place)
    {
        ShowSession(chinese, english, hint, place, Rectangle.Empty);
    }

    public void ShowSession(string chinese, string english, string hint, CardPlace place, Rectangle anchor)
    {
        var hintOnly = chinese.Length == 0 && english.Length == 0;
        var source = hintOnly ? hint : chinese;
        var same = Visible
            && place == CardPlace.Stay
            && SourceText == chinese
            && EnglishText == english
            && _source.Text == source;
        if (same)
            return;

        SourceText = chinese;
        EnglishText = english;
        _source.Text = source;
        _source.ForeColor = hintOnly ? Color.FromArgb(125, 211, 252) : Theme.Body;
        SetEnglishBody(english, Color.White, keepGloss: true);
        _status.Text = "";
        _anchoredAbove = place is CardPlace.Cursor or CardPlace.AboveInput;
        LayoutCard();
        if (place == CardPlace.TopCenter)
            PinTopCenter();
        else if (_anchoredAbove)
        {
            _movedByUser = false;
            PlaceAbove(anchor);
        }

        if (!Visible)
            Show();
        BringAbove();
    }

    public void PinTopCenter()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var x = area.Left + Math.Max(0, (area.Width - Width) / 2);
        var y = area.Top + Theme.Scale(this, 28);
        Location = new Point(x, y);
        _movedByUser = false;
        _anchoredAbove = false;
    }

    public void ShowEnglish(string english, string detail)
    {
        EnglishText = english;
        SetEnglishBody(english.Length == 0 ? detail : english, english.Length == 0 ? Theme.Error : Color.White, keepGloss: false);
        _status.Text = "";
        HideStudy();
        Relayout();
    }

    public void BeginStudy()
    {
        _studyOpen = true;
        _study.ShowLoading();
        Relayout();
    }

    public void ShowStudy(IReadOnlyList<StudyWord> words)
    {
        if (words.Count == 0)
        {
            EndStudy();
            return;
        }

        _studyOpen = true;
        _study.ShowWords(words);
        Relayout();
    }

    public void EndStudy()
    {
        if (!_studyOpen)
            return;
        HideStudy();
        Relayout();
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
        Relayout();
    }

    public void BeginWordLookup(string word)
    {
        _glossWord = word;
        _glossOpen = true;
        _gloss.ShowLoading(word);
        Relayout();
    }

    public void ShowWordGloss(WordGloss gloss)
    {
        _glossWord = gloss.Word;
        _glossOpen = true;
        _gloss.Show(gloss);
        Relayout();
    }

    public void ShowWordError(string word, string message)
    {
        _glossWord = word;
        _glossOpen = true;
        _gloss.ShowMessage(word, message);
        Relayout();
    }

    public void ShowRewriteBusy()
    {
        _gloss.ShowRewriteBusy();
        Relayout();
    }

    public void ShowRewrite(string english, string chinese)
    {
        _gloss.ShowRewrite(english, chinese);
        Relayout();
    }

    public void ShowRewriteError(string message)
    {
        _gloss.ShowRewriteError(message);
        Relayout();
    }

    public void CloseGloss()
    {
        if (!_glossOpen)
            return;
        HideGloss();
        Relayout();
    }

    void SetEnglishBody(string text, Color color, bool keepGloss)
    {
        var same = string.Equals(_english.English, text, StringComparison.Ordinal);
        if (!same || !keepGloss)
        {
            HideGloss();
            HideStudy();
        }
        _english.English = text;
        _english.ForeColor = color;
        _english.LookupEnabled = EnglishText.Length > 0 && HasLatinWord(text);
    }

    void HideGloss()
    {
        _glossWord = "";
        _glossOpen = false;
        _gloss.Clear();
    }

    void HideStudy()
    {
        _studyOpen = false;
        _study.Clear();
    }

    void Relayout()
    {
        var oldHeight = Height;
        LayoutCard();
        if (_anchoredAbove && !_movedByUser && Height != oldHeight)
            Location = new Point(Location.X, Location.Y - (Height - oldHeight));
    }

    static bool HasLatinWord(string text)
    {
        foreach (var piece in WordStudy.Split(text))
        {
            if (piece.Word)
                return true;
        }

        return false;
    }

    void LayoutCard()
    {
        if (!IsHandleCreated)
            CreateHandle();

        var scale = DeviceDpi / 96.0;
        int Sc(int value) => (int)Math.Round(value * scale);

        var pad = Sc(16);
        var gap = Sc(6);
        var closeSize = Sc(24);
        var showSource = _source.Text.Length > 0;
        var showEnglish = _english.English.Length > 0;
        var showStudy = _studyOpen && showEnglish;
        var showHint = showEnglish && _english.LookupEnabled && !_glossOpen && !showStudy;
        var showStatus = _status.Text.Length > 0;
        var showButtons = EnglishText.Length > 0;
        var showBadge = _badgeText.Length > 0;

        _badge.Text = _badgeText;
        _badge.Visible = showBadge;
        _source.Visible = showSource;
        _english.Visible = showEnglish;
        _study.Visible = showStudy;
        _hint.Visible = showHint;
        _gloss.Visible = _glossOpen && showEnglish;
        _status.Visible = showStatus;
        _copy.Visible = showButtons;
        _write.Visible = showButtons;
        _close.Visible = true;

        var measureWidth = Sc(700);
        var width = Sc(320);
        if (showSource)
            width = Math.Max(width, Measure(_source.Text, _source.Font, measureWidth).Width + pad * 2 + Sc(10));
        if (showEnglish)
            width = Math.Max(width, _english.Measure(measureWidth).Width + pad * 2);
        if (showHint)
            width = Math.Max(width, Measure(_hint.Text, _hint.Font, measureWidth).Width + pad * 2);
        if (showStatus)
            width = Math.Max(width, Measure(_status.Text, _status.Font, measureWidth).Width + pad * 2);
        if (showButtons)
            width = Math.Max(width, pad * 2 + Sc(76) + Sc(8) + Sc(116));
        if (_glossOpen && showEnglish)
            width = Math.Max(width, Sc(440));
        width = Math.Clamp(width, Sc(320), Sc(880));

        var inner = width - pad * 2;
        var y = pad;

        if (showBadge)
        {
            var badgeWidth = Measure(_badge.Text, _badge.Font, Sc(280)).Width + Sc(16);
            _badge.SetBounds(pad, y, badgeWidth, Sc(20));
            ApplyRoundRegion(_badge, Sc(10));
        }

        _close.SetBounds(width - pad - closeSize, y, closeSize, closeSize);

        y += Math.Max(showBadge ? Sc(20) : 0, closeSize) + Sc(8);

        if (showSource)
            y = Place(_source, pad, y, inner, _source.Text, _source.Font) + gap;
        if (showEnglish)
            y = PlaceEnglish(pad, y, inner) + Sc(4);
        if (showStudy)
        {
            var studyHeight = _study.Arrange(inner, Sc);
            _study.SetBounds(pad, y, inner, studyHeight);
            y += studyHeight + Sc(6);
        }

        if (showHint)
            y = Place(_hint, pad, y, inner, _hint.Text, _hint.Font) + Sc(4);
        if (_glossOpen && showEnglish)
        {
            var glossHeight = _gloss.Arrange(inner, Sc);
            _gloss.SetBounds(pad, y, inner, glossHeight);
            ApplyRoundRegion(_gloss, Sc(10));
            y += glossHeight + Sc(8);
        }

        if (showButtons)
        {
            _copy.SetBounds(pad, y + Sc(2), Sc(76), Sc(32));
            _write.SetBounds(pad + Sc(76) + Sc(8), y + Sc(2), Sc(116), Sc(32));
            y += Sc(32) + Sc(8);
        }

        if (showStatus)
            y = Place(_status, pad, y, inner, _status.Text, _status.Font);

        Height = Math.Max(y + pad, Sc(52));
        Width = width;
        ApplyRoundRegion(this, Sc(14));
        BringAbove();
    }

    int PlaceEnglish(int x, int y, int width)
    {
        var size = _english.Measure(width);
        var height = Math.Max(size.Height, _english.Font.Height + 2);
        _english.SetBounds(x, y, width, height);
        return y + height;
    }

    static int Place(Label label, int x, int y, int width, string text, Font font)
    {
        var size = Measure(text, font, width);
        var height = Math.Max(size.Height, font.Height + 2);
        label.SetBounds(x, y, width, height);
        return y + height;
    }

    static Size Measure(string text, Font font, int width)
    {
        return TextRenderer.MeasureText(
            string.IsNullOrEmpty(text) ? " " : text,
            font,
            new Size(Math.Max(10, width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    void ApplyRoundRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0)
            return;
        using var path = Theme.RoundedRect(new Rectangle(0, 0, control.Width, control.Height), radius);
        var previous = control.Region;
        control.Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundRegion(this, Theme.Scale(this, 14));
    }

    void PlaceAbove(Rectangle anchor)
    {
        var scale = DeviceDpi / 96.0;
        int Sc(int value) => (int)Math.Round(value * scale);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int x;
        int y;
        if (anchor.IsEmpty || anchor.Width < 20)
        {
            var cursor = Cursor.Position;
            area = Screen.FromPoint(cursor).WorkingArea;
            x = cursor.X;
            y = cursor.Y - Height - Sc(18);
            if (y < area.Top)
                y = cursor.Y + Sc(26);
        }
        else
        {
            area = Screen.FromRectangle(anchor).WorkingArea;
            x = anchor.Left;
            y = anchor.Top - Height - Sc(12);
            if (y < area.Top)
                y = anchor.Bottom + Sc(10);
        }

        x = Math.Min(x, area.Right - Width - Sc(10));
        x = Math.Max(x, area.Left + Sc(10));
        y = Math.Min(y, area.Bottom - Height - Sc(10));
        y = Math.Max(y, area.Top + Sc(10));
        Location = new Point(x, y);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = Theme.RoundedRect(bounds, Theme.Scale(this, 14));
        using var brush = new LinearGradientBrush(
            bounds,
            Color.FromArgb(17, 25, 40),
            Color.FromArgb(11, 18, 32),
            LinearGradientMode.Vertical);
        g.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = Theme.RoundedRect(bounds, Theme.Scale(this, 14));
        using var border = new Pen(Color.FromArgb(120, Theme.Accent));
        g.DrawPath(border, path);
        base.OnPaint(e);
    }

    void AttachDrag(Control control)
    {
        control.MouseDown += BeginDrag;
        control.MouseMove += DragMove;
        control.MouseUp += EndDrag;
    }

    void BringAbove()
    {
        if (!IsHandleCreated)
            return;
        _ = SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        _dragPending = true;
        _drag = false;
        _dragPress = Cursor.Position;
    }

    void DragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragPending)
            return;
        if (!_drag)
        {
            var dx = Cursor.Position.X - _dragPress.X;
            var dy = Cursor.Position.Y - _dragPress.Y;
            // 轻微抖动仍算点击单词，拉开一点才拖动卡片。
            if (dx * dx + dy * dy < 25)
                return;
            _drag = true;
            _dragOrigin = new Point(Location.X - Cursor.Position.X, Location.Y - Cursor.Position.Y);
        }

        Location = new Point(Cursor.Position.X + _dragOrigin.X, Cursor.Position.Y + _dragOrigin.Y);
        _movedByUser = true;
    }

    void EndDrag(object? sender, MouseEventArgs e)
    {
        _drag = false;
        _dragPending = false;
    }

    static void StyleButton(RoundButton button, string text, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 32;
        button.CornerRadius = 9;
        button.Font = new Font("Microsoft YaHei UI", 9F);
    }
}
