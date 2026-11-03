using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace InputTranslate;

/// <summary>
/// 输入框上方的翻译卡片。不抢焦点，可以拖动；拖过之后本次听写里不再自动挪位置。
/// </summary>
sealed class OverlayForm : Form
{
    const int WsExNoActivate = 0x08000000;
    const int WsExToolWindow = 0x00000080;
    const int CsDropShadow = 0x00020000;

    public enum CardPlace
    {
        Stay,
        Cursor,
        TopCenter,
        AboveInput,
    }

    public enum StatusKind
    {
        Info,
        Success,
        Error,
    }

    enum CardState
    {
        Idle,
        Listening,
        Done,
    }

    readonly Pill _pill = new();
    readonly Pill _level = new();
    readonly TextLabel _source = new();
    readonly ClickableEnglish _english = new();
    readonly TextLabel _hint = new();
    readonly StudyStrip _study = new();
    readonly WordGlossPanel _gloss = new();
    readonly TextLabel _detail = new() { SingleLine = true, AlignRight = true };
    readonly TextLabel _status = new();
    readonly RoundButton _copy = new();
    readonly RoundButton _write = new();
    readonly RoundButton _retry = new();
    readonly RoundButton _settings = new();
    readonly RoundButton _close = new();
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 3200 };
    readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 120 };

    CardState _state = CardState.Idle;
    string _idleTitle = "输入翻译";
    string _idleHint = "";
    string _englishShown = "";
    string _translatedSource = "";
    bool _stale;
    bool _translating;
    StatusKind _statusKind;
    bool _dragPending;
    bool _drag;
    Point _dragOrigin;
    Point _dragPress;
    bool _movedByUser;
    bool _anchored;
    bool _placedBelow;
    string _glossWord = "";
    bool _glossOpen;
    bool _studyOpen;
    bool _dwmCorners;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Text = "输入翻译";
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;

        _english.SuppressClick = () => _drag;
        _english.WordClicked += word => WordClicked?.Invoke(word);
        _study.WordClicked += word => WordClicked?.Invoke(word);
        _gloss.CloseClicked += (_, _) => CloseGloss();
        _gloss.RewriteClicked += (_, _) => RewriteRequested?.Invoke(this, EventArgs.Empty);
        _gloss.AdoptClicked += (_, _) => AdoptRequested?.Invoke(this, EventArgs.Empty);
        _gloss.SelectionChanged += (_, _) => Relayout();

        _copy.Text = "复制";
        _copy.Glyph = Glyphs.Copy;
        _write.Text = "写入输入框";
        _write.Glyph = Glyphs.Edit;
        _write.Variant = ButtonVariant.Primary;
        _retry.Text = "重试";
        _retry.Glyph = Glyphs.Refresh;
        _retry.Variant = ButtonVariant.Ghost;
        _settings.Variant = ButtonVariant.Ghost;
        _close.Variant = ButtonVariant.Danger;
        if (Theme.Icons(10) != null)
        {
            _settings.Glyph = Glyphs.Settings;
            _close.Glyph = Glyphs.Close;
        }
        else
        {
            _settings.Text = "设置";
            _close.Text = "×";
        }

        var tips = new ToolTip { ShowAlways = true, InitialDelay = 400 };
        tips.SetToolTip(_settings, "设置");
        tips.SetToolTip(_close, "关闭（听写中会结束本次）");
        tips.SetToolTip(_write, "用英文替换输入框里的内容");

        _copy.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);
        _write.Click += (_, _) => WriteRequested?.Invoke(this, EventArgs.Empty);
        _retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        _settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            if (_statusKind != StatusKind.Error && _status.Text.Length > 0)
            {
                _status.Text = "";
                Relayout();
            }
        };
        _pulseTimer.Tick += (_, _) => _pill.Pulse();

        Controls.AddRange(new Control[]
        {
            _pill, _level, _source, _english, _study, _hint, _gloss, _detail, _status,
            _copy, _write, _retry, _settings, _close,
        });
        foreach (var control in new Control[] { this, _pill, _level, _source, _english, _hint, _detail, _status, _study })
            AttachDrag(control);
        ApplyTheme();
    }

    public string SourceText { get; private set; } = "";

    /// <summary>当前有效的英文译文。正在更新（旧译文变灰）时为空，复制 / 写入都不可用。</summary>
    public string EnglishText => _stale ? "" : _englishShown;

    public event EventHandler? CopyRequested;
    public event EventHandler? WriteRequested;
    public event Action<string>? WordClicked;
    public event EventHandler? RewriteRequested;
    public event EventHandler? AdoptRequested;
    public event EventHandler? RetryRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? CloseRequested;

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
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _dwmCorners = TryDwmCorners();
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

    /// <summary>主题或文字大小改了以后重新上色、重新排版。</summary>
    public void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Card;
        _english.BackColor = p.Card;
        _english.Font = Theme.CardEnglish(14.5F);
        _source.Font = Theme.Card(10.5F);
        _hint.Font = Theme.Card(8.5F);
        _hint.ForeColor = p.Dim;
        _detail.Font = Theme.Card(8F);
        _detail.ForeColor = p.Dim;
        _status.Font = Theme.Card(8.5F);
        _pill.Font = Theme.Card(8.5F, FontStyle.Bold);
        _level.Font = Theme.Card(8F);
        foreach (var button in new[] { _copy, _write, _retry })
            button.Font = Theme.Card(9F);
        _settings.Font = Theme.Card(10F);
        _close.Font = Theme.Card(10F);
        _study.ApplyTheme();
        _gloss.ApplyTheme();
        UpdateColors();
        UpdateDwmBorder();
        if (IsHandleCreated)
            Relayout();
        Invalidate(true);
    }

    /// <summary>空闲提示卡片：程序刚启动、托盘「显示卡片」时用。</summary>
    public void ShowIdle(string title, string hint, CardPlace place)
    {
        _state = CardState.Idle;
        _idleTitle = title;
        _idleHint = hint;
        SourceText = "";
        _englishShown = "";
        _translatedSource = "";
        _stale = false;
        _detail.Text = "";
        SetTranslating(false);
        ClearStatus();
        HideGloss();
        HideStudy();
        Present(place, Rectangle.Empty);
    }

    /// <summary>开始一次听写。</summary>
    public void BeginListening(string level, string hint)
    {
        _state = CardState.Listening;
        _level.Text = level;
        _idleHint = hint;
        SourceText = "";
        _englishShown = "";
        _translatedSource = "";
        _stale = false;
        _detail.Text = "";
        _movedByUser = false;
        SetTranslating(false);
        ClearStatus();
        HideGloss();
        HideStudy();
        Present(CardPlace.Cursor, Rectangle.Empty);
    }

    /// <summary>输入框里的中文变了。旧译文先变灰留着，避免卡片忽大忽小。</summary>
    public void UpdateSource(string chinese, Rectangle anchor)
    {
        if (SourceText == chinese)
            return;
        SourceText = chinese;
        var stale = _englishShown.Length > 0 && !string.Equals(chinese, _translatedSource, StringComparison.Ordinal);
        if (stale != _stale)
        {
            _stale = stale;
            if (stale)
            {
                HideGloss();
                HideStudy();
            }
        }

        if (_statusKind == StatusKind.Error)
            ClearStatus();
        Present(chinese.Length == 0 ? CardPlace.Stay : CardPlace.AboveInput, anchor);
    }

    public void SetTranslating(bool translating)
    {
        if (_translating == translating)
            return;
        _translating = translating;
        if (translating)
            _pulseTimer.Start();
        else
            _pulseTimer.Stop();
        UpdatePill();
        if (Visible)
            Relayout();
    }

    public void ShowTranslation(string chinese, string english, string detail)
    {
        SourceText = chinese;
        _translatedSource = chinese;
        _englishShown = english;
        _stale = false;
        _detail.Text = detail;
        if (_statusKind == StatusKind.Error)
            ClearStatus();
        HideGloss();
        HideStudy();
        Present(CardPlace.Stay, Rectangle.Empty);
    }

    public void ShowTranslationError(string message)
    {
        SetStatus(message, StatusKind.Error);
    }

    /// <summary>本次听写结束：保留卡片内容，方便复制。</summary>
    public void FinishSession()
    {
        _state = CardState.Done;
        Present(CardPlace.Stay, Rectangle.Empty);
    }

    public void SetStatus(string text, StatusKind kind = StatusKind.Info)
    {
        _status.Text = text;
        _statusKind = kind;
        _statusTimer.Stop();
        if (text.Length > 0 && kind != StatusKind.Error)
            _statusTimer.Start();
        if (Visible)
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

    public void BeginWordLookup(string word)
    {
        _glossWord = word;
        _glossOpen = true;
        _english.Marked = word;
        _gloss.ShowLoading(word);
        Relayout();
    }

    public void ShowWordGloss(WordGloss gloss)
    {
        _glossWord = gloss.Word;
        _glossOpen = true;
        _english.Marked = gloss.Word;
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

    void Present(CardPlace place, Rectangle anchor)
    {
        var wasVisible = Visible;
        var oldHeight = Height;
        UpdateColors();
        LayoutCard();
        switch (place)
        {
            case CardPlace.TopCenter:
                PinTopCenter();
                break;
            case CardPlace.Cursor:
                _anchored = true;
                PlaceAbove(Rectangle.Empty);
                break;
            // 读不到输入框位置时不跟着鼠标跑，留在原地。
            case CardPlace.AboveInput when !_movedByUser && !anchor.IsEmpty:
                _anchored = true;
                PlaceAbove(anchor);
                break;
            default:
                // 放在输入框上方时，卡片变高要往上长，不能盖住输入框。
                if (wasVisible && _anchored && !_movedByUser && !_placedBelow && Height != oldHeight)
                    Location = new Point(Location.X, Location.Y - (Height - oldHeight));
                break;
        }

        KeepOnScreen();
        if (!Visible)
            Show();
        BringAbove();
        Invalidate(true);
    }

    void PinTopCenter()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = area.Left + Math.Max(0, (area.Width - Width) / 2);
        var y = area.Top + Theme.Scale(this, 28);
        Location = new Point(x, y);
        _movedByUser = false;
        _anchored = false;
        _placedBelow = true;
    }

    void HideGloss()
    {
        _glossWord = "";
        _glossOpen = false;
        _english.Marked = "";
        _gloss.Clear();
    }

    void HideStudy()
    {
        _studyOpen = false;
        _study.Clear();
    }

    void ClearStatus()
    {
        _statusTimer.Stop();
        _status.Text = "";
        _statusKind = StatusKind.Info;
    }

    void UpdatePill()
    {
        var p = Theme.Current;
        if (_translating)
        {
            _pill.Set("翻译中", p.Accent, true);
            return;
        }

        switch (_state)
        {
            case CardState.Listening:
                _pill.Set("正在听写", p.Success, false);
                break;
            case CardState.Done:
                _pill.Set(EnglishText.Length > 0 ? "已完成" : "已结束", p.Muted, false);
                break;
            default:
                _pill.Set(_idleTitle, p.Accent, false);
                break;
        }
    }

    void UpdateColors()
    {
        var p = Theme.Current;
        UpdatePill();
        _pill.BackColor = p.Card;
        _pill.ForeColor = p.Body;
        _pill.Fill = Color.Empty;
        _level.BackColor = p.Card;
        _level.ForeColor = p.Muted;
        _level.Fill = p.Surface;
        var hintOnly = SourceText.Length == 0;
        _source.ForeColor = hintOnly ? (_state == CardState.Idle ? p.Body : p.Muted) : p.Body;
        _status.ForeColor = _statusKind switch
        {
            StatusKind.Error => p.Error,
            StatusKind.Success => p.Success,
            _ => p.Muted,
        };

        var english = EnglishText.Length > 0;
        if (_englishShown.Length > 0)
        {
            _english.English = _englishShown;
            _english.ForeColor = _stale ? p.Dim : p.Text;
        }
        else if (_translating)
        {
            _english.English = "…";
            _english.ForeColor = p.Dim;
        }
        else
        {
            _english.English = "";
        }

        _english.LookupEnabled = english && HasLatinWord(_englishShown);
        _copy.Enabled = english;
        _write.Enabled = english;
    }

    void Relayout()
    {
        if (!IsHandleCreated)
            return;
        var oldHeight = Height;
        UpdateColors();
        LayoutCard();
        if (_anchored && !_movedByUser && !_placedBelow && Height != oldHeight)
            Location = new Point(Location.X, Location.Y - (Height - oldHeight));
        KeepOnScreen();
        Invalidate(true);
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

        var pad = Sc(14);
        var gap = Sc(6);
        var iconSize = Sc(26);
        var hasSource = SourceText.Length > 0;
        var sourceText = hasSource ? SourceText : _idleHint;
        var showSource = sourceText.Length > 0;
        var showEnglish = _english.English.Length > 0;
        var hasEnglish = EnglishText.Length > 0;
        var showStudy = _studyOpen && hasEnglish;
        var showGloss = _glossOpen && hasEnglish;
        var showButtons = _englishShown.Length > 0;
        var showStatus = _status.Text.Length > 0;
        var showRetry = showStatus && _statusKind == StatusKind.Error;
        var showLevel = _level.Text.Length > 0 && _state != CardState.Idle;

        var hint = "";
        if (_state == CardState.Listening)
            hint = hasEnglish && !_glossOpen ? "点单词查释义 · Enter 完成 · Esc 取消" : hasSource ? "Enter 完成 · Esc 取消 · Shift+Enter 换行" : "";
        else if (hasEnglish && _english.LookupEnabled && !_glossOpen && !showStudy)
            hint = "点单词查释义";
        _hint.Text = hint;
        var showHint = hint.Length > 0;

        _source.Text = sourceText;
        _pill.Visible = true;
        _level.Visible = showLevel;
        _source.Visible = showSource;
        _english.Visible = showEnglish;
        _study.Visible = showStudy;
        _hint.Visible = showHint;
        _gloss.Visible = showGloss;
        _status.Visible = showStatus;
        _retry.Visible = showRetry;
        _copy.Visible = showButtons;
        _write.Visible = showButtons;
        _detail.Visible = showButtons && _detail.Text.Length > 0 && !_stale;

        var measureWidth = Sc(600);
        var width = Sc(340);
        if (showSource)
            width = Math.Max(width, Measure(sourceText, _source.Font, measureWidth).Width + pad * 2 + Sc(4));
        if (showEnglish)
            width = Math.Max(width, _english.Measure(measureWidth).Width + pad * 2);
        if (showHint)
            width = Math.Max(width, Measure(hint, _hint.Font, measureWidth).Width + pad * 2);
        if (showStatus)
            width = Math.Max(width, Measure(_status.Text, _status.Font, measureWidth).Width + pad * 2 + (showRetry ? _retry.PreferredWidth(Sc(8)) + Sc(8) : 0));
        if (showGloss)
            width = Math.Max(width, Sc(440));
        width = Math.Min(width, Sc(660));
        var inner = width - pad * 2;

        // 标题行：状态 + 难度，右边是设置和关闭。
        var y = Sc(10);
        var pillSize = _pill.Measure(Sc);
        _pill.SetBounds(pad - Sc(2), y + (iconSize - pillSize.Height) / 2, pillSize.Width, pillSize.Height);
        if (showLevel)
        {
            var levelSize = _level.Measure(Sc);
            _level.SetBounds(_pill.Right + Sc(4), y + (iconSize - levelSize.Height) / 2, levelSize.Width, levelSize.Height);
        }

        _close.SetBounds(width - Sc(8) - iconSize, y, iconSize, iconSize);
        _settings.SetBounds(_close.Left - Sc(2) - iconSize, y, iconSize, iconSize);
        y += iconSize + Sc(6);

        if (showSource)
            y = Place(_source, pad, y, inner) + Sc(4);
        if (showEnglish)
        {
            var size = _english.Measure(inner);
            var height = Math.Max(size.Height, _english.Font.Height + 2);
            _english.SetBounds(pad, y, inner, height);
            y += height + Sc(6);
        }

        if (showStudy)
        {
            var studyHeight = _study.Arrange(inner, Sc);
            _study.SetBounds(pad, y, inner, studyHeight);
            y += studyHeight + Sc(8);
        }

        if (showGloss)
        {
            var glossHeight = _gloss.Arrange(inner, Sc);
            _gloss.SetBounds(pad, y, inner, glossHeight);
            y += glossHeight + Sc(10);
        }

        if (showButtons)
        {
            var buttonHeight = Sc(30);
            var copyWidth = _copy.PreferredWidth(Sc(12));
            var writeWidth = _write.PreferredWidth(Sc(12));
            _copy.SetBounds(pad, y, copyWidth, buttonHeight);
            _write.SetBounds(_copy.Right + Sc(8), y, writeWidth, buttonHeight);
            if (_detail.Visible)
            {
                var left = _write.Right + Sc(10);
                _detail.SetBounds(left, y, Math.Max(0, pad + inner - left), buttonHeight);
                _detail.Visible = _detail.Width > Sc(40);
            }

            y += buttonHeight + Sc(8);
        }

        if (showHint)
            y = Place(_hint, pad, y, inner) + Sc(2);

        if (showStatus)
        {
            var retryWidth = showRetry ? _retry.PreferredWidth(Sc(8)) : 0;
            var textWidth = showRetry ? inner - retryWidth - Sc(8) : inner;
            var top = y;
            y = Place(_status, pad, y, textWidth);
            if (showRetry)
            {
                var retryHeight = Sc(24);
                _retry.SetBounds(pad + inner - retryWidth, top - Sc(3), retryWidth, retryHeight);
                y = Math.Max(y, top + retryHeight - Sc(3));
            }
        }

        Height = Math.Max(y + Sc(10), Sc(56));
        Width = width;
        if (!_dwmCorners)
            ApplyRoundRegion(Sc(12));
        BringAbove();
    }

    static int Place(TextLabel label, int x, int y, int width)
    {
        var height = label.Measure(width).Height;
        label.SetBounds(x, y, width, height);
        return y + height;
    }

    static Size Measure(string text, Font font, int width)
    {
        return TextRenderer.MeasureText(
            string.IsNullOrEmpty(text) ? " " : text,
            font,
            new Size(Math.Max(10, width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    void ApplyRoundRegion(int radius)
    {
        if (Width <= 0 || Height <= 0)
            return;
        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), radius);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    void PlaceAbove(Rectangle anchor)
    {
        var scale = DeviceDpi / 96.0;
        int Sc(int value) => (int)Math.Round(value * scale);

        Rectangle area;
        int x;
        int y;
        if (anchor.IsEmpty || anchor.Width < 20)
        {
            var cursor = Cursor.Position;
            area = Screen.FromPoint(cursor).WorkingArea;
            x = cursor.X - Sc(24);
            y = cursor.Y - Height - Sc(18);
            _placedBelow = y < area.Top;
            if (_placedBelow)
                y = cursor.Y + Sc(26);
        }
        else
        {
            area = Screen.FromRectangle(anchor).WorkingArea;
            x = anchor.Left;
            y = anchor.Top - Height - Sc(10);
            _placedBelow = y < area.Top;
            if (_placedBelow)
                y = anchor.Bottom + Sc(10);
        }

        Location = new Point(x, y);
    }

    /// <summary>卡片变高以后可能伸出屏幕，拉回工作区内。</summary>
    void KeepOnScreen()
    {
        var margin = Theme.Scale(this, 8);
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        var x = Math.Max(area.Left + margin, Math.Min(Left, area.Right - Width - margin));
        var y = Math.Max(area.Top + margin, Math.Min(Top, area.Bottom - Height - margin));
        if (x != Left || y != Top)
            Location = new Point(x, y);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Current.Card);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_dwmCorners)
            return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = Theme.RoundedRect(bounds, Theme.Scale(this, 12));
        using var border = new Pen(Theme.Current.CardBorder);
        g.DrawPath(border, path);
    }

    void AttachDrag(Control control)
    {
        control.MouseDown += BeginDrag;
        control.MouseMove += DragMove;
        control.MouseUp += EndDrag;
    }

    void BringAbove()
    {
        if (!IsHandleCreated || !Visible)
            return;
        _ = SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
    }

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

    // Windows 11 由 DWM 画平滑的圆角和边框；Windows 10 退回到窗口区域裁剪。
    bool TryDwmCorners()
    {
        if (Environment.OSVersion.Version.Build < 22000)
            return false;
        try
        {
            var preference = 2; // DWMWCP_ROUND
            if (DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)) != 0)
                return false;
            Region = null;
            UpdateDwmBorder();
            return true;
        }
        catch
        {
            return false;
        }
    }

    void UpdateDwmBorder()
    {
        if (!_dwmCorners || !IsHandleCreated)
            return;
        var c = Theme.Current.CardBorder;
        var colorRef = c.R | (c.G << 8) | (c.B << 16);
        _ = DwmSetWindowAttribute(Handle, 34, ref colorRef, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            _pulseTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>卡片标题行上的小标签：可选的圆点 + 文字，翻译中时圆点会呼吸。</summary>
sealed class Pill : Control
{
    Color _dot = Color.Empty;
    bool _animate;
    int _phase;

    public Pill()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        TabStop = false;
    }

    /// <summary>底色；Color.Empty 表示不画底。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Color.Empty;

    public void Set(string text, Color dot, bool animate)
    {
        if (Text == text && _dot == dot && _animate == animate)
            return;
        Text = text;
        _dot = dot;
        _animate = animate;
        Invalidate();
    }

    public void Pulse()
    {
        if (!_animate)
            return;
        _phase = (_phase + 1) % 16;
        Invalidate();
    }

    public Size Measure(Func<int, int> scale)
    {
        var text = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var dot = _dot.IsEmpty ? 0 : scale(14);
        var padX = Fill.IsEmpty ? scale(2) : scale(8);
        return new Size(text.Width + dot + padX * 2 + 2, Math.Max(text.Height + scale(6), scale(20)));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var padX = Fill.IsEmpty ? Theme.Scale(this, 2) : Theme.Scale(this, 8);
        if (!Fill.IsEmpty)
        {
            using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
            using var fill = new SolidBrush(Fill);
            g.FillPath(fill, path);
        }

        var x = padX;
        if (!_dot.IsEmpty)
        {
            var size = Theme.Scale(this, 8);
            var alpha = _animate ? 110 + (int)(145 * Math.Abs(Math.Sin(_phase * Math.PI / 16))) : 255;
            using var brush = new SolidBrush(Color.FromArgb(alpha, _dot));
            g.FillEllipse(brush, x, (Height - size) / 2f, size, size);
            x += Theme.Scale(this, 14);
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), ForeColor,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
    }
}
