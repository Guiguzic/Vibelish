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
        BottomCenter,
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

    readonly StatusLine _header = new();
    readonly TextLabel _hint = new() { SingleLine = true, AlignRight = true };
    readonly TextLabel _source = new();
    readonly ClickableEnglish _english = new();
    readonly TextLabel _error = new();
    readonly StudyStrip _study = new();
    readonly WordGlossPanel _gloss = new();
    readonly TextLabel _meta = new() { SingleLine = true, AlignRight = true };
    readonly RoundButton _copy = new();
    readonly RoundButton _write = new();
    readonly RoundButton _speak = new();
    readonly RoundButton _retry = new();
    readonly RoundButton _settings = new();
    readonly RoundButton _close = new();
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2600 };
    readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 90 };

    CardState _state = CardState.Idle;
    string _idleTitle = "输入翻译";
    string _idleHint = "";
    string _level = "";
    string _englishShown = "";
    string _translatedSource = "";
    string _detail = "";
    string _errorText = "";
    string _status = "";
    bool _stale;
    bool _translating;
    bool _streaming;
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
    int _footerTop = -1;

    // 显示选项（设置 → 卡片与外观 / 单词与朗读）。
    bool _showSource = true;
    bool _showHints = true;
    bool _showEngine = true;
    bool _clickLookup = true;
    bool _showSpeak = true;
    string _position = "input";

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
        _gloss.SpeakClicked += (_, _) => SpeakWordRequested?.Invoke(_glossWord);
        _gloss.SelectionChanged += (_, _) => Relayout();

        _write.Text = "写入输入框";
        _write.Glyph = Glyphs.Edit;
        _write.Variant = ButtonVariant.Primary;
        _copy.Text = "复制";
        _copy.Glyph = Glyphs.Copy;
        _speak.Glyph = Glyphs.Speaker;
        _retry.Text = "重试";
        _retry.Variant = ButtonVariant.Link;
        _settings.Variant = ButtonVariant.Ghost;
        _close.Variant = ButtonVariant.Ghost;
        foreach (var button in new[] { _write, _copy, _speak })
            button.CornerRadius = 99;
        if (Theme.Icons(10) != null)
        {
            _settings.Glyph = Glyphs.Settings;
            _close.Glyph = Glyphs.Close;
        }
        else
        {
            _settings.Text = "设置";
            _close.Text = "×";
            _speak.Text = "朗读";
        }

        var tips = new ToolTip { ShowAlways = true, InitialDelay = 400 };
        tips.SetToolTip(_settings, "设置");
        tips.SetToolTip(_close, "关闭（听写中会结束本次）");
        tips.SetToolTip(_write, "用英文替换输入框里的内容");
        tips.SetToolTip(_speak, "朗读英文");

        _copy.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);
        _write.Click += (_, _) => WriteRequested?.Invoke(this, EventArgs.Empty);
        _speak.Click += (_, _) => SpeakRequested?.Invoke(this, EventArgs.Empty);
        _retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        _settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            if (_statusKind != StatusKind.Error && _status.Length > 0)
            {
                _status = "";
                Relayout();
            }
        };
        _pulseTimer.Tick += (_, _) =>
        {
            _header.Pulse();
            _english.Tick();
        };

        Controls.AddRange(new Control[]
        {
            _header, _hint, _source, _english, _error, _study, _gloss, _meta,
            _write, _copy, _speak, _retry, _settings, _close,
        });
        foreach (var control in new Control[] { this, _header, _hint, _source, _english, _error, _meta })
            AttachDrag(control);
        ApplyTheme();
    }

    public string SourceText { get; private set; } = "";

    /// <summary>当前有效的英文译文。正在更新（旧译文变灰）或还在逐字输出时为空，复制 / 写入都不可用。</summary>
    public string EnglishText => _stale || _streaming ? "" : _englishShown;

    /// <summary>鼠标停在卡片上（自动收起时要等一等）。</summary>
    public bool PointerOver => Visible && Bounds.Contains(Cursor.Position);

    public event EventHandler? CopyRequested;
    public event EventHandler? WriteRequested;
    public event EventHandler? SpeakRequested;
    public event Action<string>? SpeakWordRequested;
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

    /// <summary>主题、强调色或文字大小改了以后重新上色、重新排版。</summary>
    public void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Card;
        _english.BackColor = p.Card;
        _english.Font = Theme.CardEnglish(15F);
        _source.Font = Theme.Card(10F);
        _error.Font = Theme.Card(9F);
        _hint.Font = Theme.Card(8.25F);
        _meta.Font = Theme.Card(8.25F);
        _header.Font = Theme.Card(8.75F);
        _header.LevelFont = Theme.Card(8.75F);
        foreach (var button in new[] { _copy, _write })
            button.Font = Theme.Card(9F);
        _speak.Font = Theme.Card(9.5F);
        _retry.Font = Theme.Card(9F);
        _settings.Font = Theme.Card(9.5F);
        _close.Font = Theme.Card(9.5F);
        _study.ApplyTheme();
        _gloss.ApplyTheme();
        UpdateColors();
        UpdateDwmBorder();
        if (IsHandleCreated)
            Relayout();
        Invalidate(true);
    }

    /// <summary>卡片上显示哪些东西、放在哪里。</summary>
    public void ApplyOptions(AppConfig config)
    {
        _showSource = config.ShowSource;
        _showHints = config.ShowHints;
        _showEngine = config.ShowEngine;
        _clickLookup = config.ClickLookup;
        _showSpeak = config.ShowSpeak;
        _position = config.CardPosition;
        _study.Style = config.StudyStyle;
        if (IsHandleCreated && Visible)
            Relayout();
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
        _streaming = false;
        _detail = "";
        _errorText = "";
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
        _level = level;
        _idleHint = hint;
        SourceText = "";
        _englishShown = "";
        _translatedSource = "";
        _stale = false;
        _streaming = false;
        _detail = "";
        _errorText = "";
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

        _errorText = "";
        Present(chinese.Length == 0 ? CardPlace.Stay : CardPlace.AboveInput, anchor);
    }

    public void SetTranslating(bool translating)
    {
        if (_translating == translating)
            return;
        _translating = translating;
        if (!translating)
            _streaming = false;
        UpdateTimers();
        if (Visible)
            Relayout();
    }

    /// <summary>流式输出：译文一段一段到，先显示出来，结束后再调 <see cref="ShowTranslation"/>。</summary>
    public void ShowPartial(string chinese, string english)
    {
        if (english.Length == 0)
            return;
        SourceText = chinese;
        _translatedSource = chinese;
        _englishShown = english;
        _stale = false;
        _streaming = true;
        _errorText = "";
        HideGloss();
        HideStudy();
        UpdateTimers();
        Present(CardPlace.Stay, Rectangle.Empty);
    }

    public void ShowTranslation(string chinese, string english, string detail)
    {
        SourceText = chinese;
        _translatedSource = chinese;
        _englishShown = english;
        _stale = false;
        _streaming = false;
        _detail = detail;
        _errorText = "";
        if (_statusKind == StatusKind.Error)
            ClearStatus();
        HideGloss();
        HideStudy();
        UpdateTimers();
        Present(CardPlace.Stay, Rectangle.Empty);
    }

    public void ShowTranslationError(string message)
    {
        _streaming = false;
        _errorText = message;
        UpdateTimers();
        if (Visible)
            Relayout();
    }

    /// <summary>本次听写结束：保留卡片内容，方便复制。</summary>
    public void FinishSession()
    {
        _state = CardState.Done;
        Present(CardPlace.Stay, Rectangle.Empty);
    }

    public void SetStatus(string text, StatusKind kind = StatusKind.Info)
    {
        if (kind == StatusKind.Error)
        {
            _errorText = text;
            if (Visible)
                Relayout();
            return;
        }

        _status = text;
        _statusKind = kind;
        _statusTimer.Stop();
        if (text.Length > 0)
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

    void UpdateTimers()
    {
        var animate = _translating || _streaming;
        _english.Caret = _streaming;
        if (animate)
            _pulseTimer.Start();
        else
            _pulseTimer.Stop();
    }

    void Present(CardPlace place, Rectangle anchor)
    {
        var wasVisible = Visible;
        var oldHeight = Height;
        UpdateColors();
        LayoutCard();

        // 设置里选了「屏幕顶部 / 底部」时，不再跟着输入框跑。
        if (place is CardPlace.Cursor or CardPlace.AboveInput && !_movedByUser)
        {
            if (_position == "top")
                place = CardPlace.TopCenter;
            else if (_position == "bottom")
                place = CardPlace.BottomCenter;
        }

        switch (place)
        {
            case CardPlace.TopCenter:
                PinTopCenter();
                break;
            case CardPlace.BottomCenter:
                PinBottomCenter();
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

    void PinBottomCenter()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = area.Left + Math.Max(0, (area.Width - Width) / 2);
        var y = area.Bottom - Height - Theme.Scale(this, 36);
        Location = new Point(x, y);
        _movedByUser = false;
        _anchored = true;
        _placedBelow = false;
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
        _status = "";
        _statusKind = StatusKind.Info;
    }

    void UpdateHeader()
    {
        var p = Theme.Current;
        if (_translating || _streaming)
            _header.Set("翻译中", p.Accent, true, _level);
        else if (_state == CardState.Listening)
            _header.Set("听写中", p.Success, false, _level);
        else if (_state == CardState.Done)
            _header.Set(_englishShown.Length > 0 ? "已完成" : "已结束", p.Dim, false, _level);
        else
            _header.Set(_idleTitle, p.Accent, false, "");
    }

    void UpdateColors()
    {
        var p = Theme.Current;
        UpdateHeader();
        _header.BackColor = p.Card;
        _header.ForeColor = p.Body;
        _header.LevelColor = p.Dim;
        _hint.ForeColor = p.Dim;
        _source.ForeColor = SourceText.Length == 0 && _state == CardState.Idle ? p.Body : p.Muted;
        _error.ForeColor = p.Error;
        _meta.ForeColor = _status.Length > 0
            ? _statusKind == StatusKind.Success ? p.Success : p.Muted
            : p.Dim;

        var english = EnglishText.Length > 0;
        _english.English = _englishShown;
        _english.ForeColor = _stale ? p.Dim : p.Text;
        _english.Loading = _englishShown.Length == 0 && _translating;
        _english.LookupEnabled = english && _clickLookup && HasLatinWord(_englishShown);
        _copy.Enabled = english;
        _write.Enabled = english;
        _speak.Enabled = english;
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

    string HintText()
    {
        if (!_showHints)
            return "";
        var hasEnglish = EnglishText.Length > 0;
        if (_state == CardState.Listening)
        {
            if (SourceText.Length == 0)
                return "Esc 取消";
            return hasEnglish && _english.LookupEnabled && !_glossOpen
                ? "点词查义 · Enter 完成 · Esc 取消"
                : "Enter 完成 · Shift+Enter 换行 · Esc 取消";
        }

        if (_state == CardState.Done && hasEnglish && _english.LookupEnabled && !_glossOpen)
            return "点单词查释义";
        return "";
    }

    void LayoutCard()
    {
        if (!IsHandleCreated)
            CreateHandle();

        var scale = DeviceDpi / 96.0;
        int Sc(int value) => (int)Math.Round(value * scale);

        var padX = Sc(18);
        var icon = Sc(26);
        var hasEnglish = EnglishText.Length > 0;
        var sourceText = SourceText.Length > 0 ? (_showSource ? SourceText : "") : _idleHint;
        var showSource = sourceText.Length > 0;
        var showEnglish = _englishShown.Length > 0 || _english.Loading;
        var showError = _errorText.Length > 0;
        var showStudy = _studyOpen && hasEnglish;
        var showGloss = _glossOpen && hasEnglish;
        var showFooter = _englishShown.Length > 0;
        var hint = HintText();

        _source.Text = sourceText;
        _error.Text = _errorText;
        _hint.Text = hint;
        _source.Visible = showSource;
        _english.Visible = showEnglish;
        _error.Visible = showError;
        _retry.Visible = showError && _state != CardState.Idle;
        _study.Visible = showStudy;
        _gloss.Visible = showGloss;
        _copy.Visible = showFooter;
        _write.Visible = showFooter;
        _speak.Visible = showFooter && _showSpeak;
        var meta = _status.Length > 0 ? _status : _showEngine && !_stale && !_streaming ? _detail : "";
        _meta.Text = meta;
        _meta.Visible = showFooter && meta.Length > 0;

        var measureWidth = Sc(580);
        var width = Sc(380);
        if (showSource)
            width = Math.Max(width, Measure(sourceText, _source.Font, measureWidth).Width + padX * 2 + Sc(4));
        if (showEnglish)
            width = Math.Max(width, _english.Measure(measureWidth).Width + padX * 2 + Sc(6));
        if (showError)
            width = Math.Max(width, Measure(_errorText, _error.Font, measureWidth).Width + padX * 2 + _retry.PreferredWidth(Sc(8)) + Sc(8));
        if (showGloss)
            width = Math.Max(width, Sc(460));
        if (_streaming && Visible)
            width = Math.Max(width, Width);
        width = Math.Min(width, Sc(620));
        var inner = width - padX * 2;

        // 标题行：状态点 + 文字 + 难度；右边是提示、设置和关闭。
        var y = Sc(10);
        var headerSize = _header.Measure();
        _header.SetBounds(padX - Sc(1), y + (icon - headerSize.Height) / 2, headerSize.Width, headerSize.Height);
        _close.SetBounds(width - Sc(8) - icon, y, icon, icon);
        _settings.SetBounds(_close.Left - icon, y, icon, icon);
        var hintRoom = _settings.Left - Sc(8) - (_header.Right + Sc(16));
        var hintWidth = hint.Length == 0 ? 0 : _hint.Measure(measureWidth).Width;
        _hint.Visible = hint.Length > 0 && hintWidth <= hintRoom;
        if (_hint.Visible)
            _hint.SetBounds(_settings.Left - Sc(6) - hintWidth, y, hintWidth, icon);
        y += icon + Sc(4);

        if (showSource)
            y = Place(_source, padX, y, inner) + Sc(6);
        if (showEnglish)
        {
            var size = _english.Measure(inner);
            var height = Math.Max(size.Height, _english.Font.Height + 2);
            _english.SetBounds(padX, y, inner, height);
            y += height;
        }

        if (showError)
        {
            y += Sc(8);
            var retryWidth = _retry.Visible ? _retry.PreferredWidth(Sc(8)) : 0;
            var textWidth = inner - (retryWidth > 0 ? retryWidth + Sc(8) : 0);
            var top = y;
            y = Place(_error, padX, y, textWidth);
            if (_retry.Visible)
            {
                var retryHeight = Sc(24);
                _retry.SetBounds(padX + inner - retryWidth, top - Sc(4), retryWidth, retryHeight);
                y = Math.Max(y, top + retryHeight - Sc(4));
            }
        }

        if (showStudy)
        {
            y += Sc(12);
            var studyHeight = _study.Arrange(inner, Sc);
            _study.SetBounds(padX, y, inner, studyHeight);
            y += studyHeight;
        }

        if (showGloss)
        {
            y += Sc(12);
            var glossHeight = _gloss.Arrange(inner, Sc);
            _gloss.SetBounds(padX, y, inner, glossHeight);
            y += glossHeight;
        }

        _footerTop = -1;
        if (showFooter)
        {
            y += Sc(14);
            _footerTop = y;
            y += Sc(12);
            var buttonHeight = Sc(30);
            var writeWidth = _write.PreferredWidth(Sc(14));
            var copyWidth = _copy.PreferredWidth(Sc(14));
            _write.SetBounds(padX, y, writeWidth, buttonHeight);
            _copy.SetBounds(_write.Right + Sc(8), y, copyWidth, buttonHeight);
            var right = _copy.Right;
            if (_speak.Visible)
            {
                _speak.SetBounds(_copy.Right + Sc(8), y, buttonHeight, buttonHeight);
                right = _speak.Right;
            }

            if (_meta.Visible)
            {
                var left = right + Sc(12);
                _meta.SetBounds(left, y, Math.Max(0, padX + inner - left), buttonHeight);
                _meta.Visible = _meta.Width > Sc(40);
            }

            y += buttonHeight;
        }

        Height = Math.Max(y + Sc(14), Sc(58));
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
        var g = e.Graphics;
        var p = Theme.Current;
        if (_footerTop >= 0)
        {
            using var line = new Pen(p.Separator);
            var pad = Theme.Scale(this, 18);
            g.DrawLine(line, pad, _footerTop, Width - pad, _footerTop);
        }

        if (_dwmCorners)
            return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = Theme.RoundedRect(bounds, Theme.Scale(this, 12));
        using var border = new Pen(p.CardBorder);
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
        var colorRef = Theme.ColorRef(Theme.Current.CardBorder);
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

/// <summary>卡片左上角的状态：一个圆点 + 文字 + 难度。翻译中圆点会呼吸。</summary>
sealed class StatusLine : Control
{
    Color _dot = Color.Empty;
    bool _animate;
    int _phase;
    string _level = "";

    public StatusLine()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        TabStop = false;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Font? LevelFont { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color LevelColor { get; set; } = Color.Gray;

    public void Set(string text, Color dot, bool animate, string level)
    {
        if (Text == text && _dot == dot && _animate == animate && _level == level)
            return;
        Text = text;
        _dot = dot;
        _animate = animate;
        _level = level ?? "";
        Invalidate();
    }

    public void Pulse()
    {
        if (!_animate)
            return;
        _phase = (_phase + 1) % 24;
        Invalidate();
    }

    string Suffix => _level.Length == 0 ? "" : "  ·  " + _level;

    public Size Measure()
    {
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        var text = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, new Size(int.MaxValue, int.MaxValue), flags);
        var suffix = Suffix.Length == 0 ? Size.Empty
            : TextRenderer.MeasureText(Suffix, LevelFont ?? Font, new Size(int.MaxValue, int.MaxValue), flags);
        var dot = Theme.Scale(this, 14);
        return new Size(dot + text.Width + suffix.Width + Theme.Scale(this, 4), Math.Max(text.Height, Theme.Scale(this, 18)));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var x = Theme.Scale(this, 1);
        if (!_dot.IsEmpty)
        {
            var size = Theme.Scale(this, 7);
            var alpha = _animate ? 90 + (int)(165 * (0.5 + 0.5 * Math.Cos(_phase * Math.PI / 12))) : 255;
            if (_animate)
            {
                var halo = size + Theme.Scale(this, 4);
                using var glow = new SolidBrush(Color.FromArgb(Math.Max(0, alpha / 4), _dot));
                g.FillEllipse(glow, x - (halo - size) / 2f, (Height - halo) / 2f, halo, halo);
            }

            using var brush = new SolidBrush(Color.FromArgb(alpha, _dot));
            g.FillEllipse(brush, x, (Height - size) / 2f, size, size);
            x += Theme.Scale(this, 14);
        }

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        var textWidth = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), flags).Width;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textWidth + 2, Height), ForeColor, flags);
        if (Suffix.Length > 0)
            TextRenderer.DrawText(g, Suffix, LevelFont ?? Font, new Rectangle(x + textWidth, 0, Math.Max(1, Width - x - textWidth), Height), LevelColor, flags);
    }
}
