using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace InputTranslate;

sealed class TrayApplicationContext : ApplicationContext
{
    const int RecentLimit = 8;

    readonly OverlayForm _overlay = new();
    readonly NotifyIcon _icon;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 9000 };
    readonly Icon _appIcon;
    readonly Icon _listeningIcon;
    readonly Translator _translator;
    readonly ImeCapture _ime;
    readonly GlobalHotkey _hotkey;
    readonly FieldWatcher _watcher;
    readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, @"Local\InputTranslate.Show");
    readonly ManualResetEvent _stopping = new(false);
    readonly Thread _showThread;
    readonly List<(string Chinese, string English)> _recent = new();
    readonly ToolStripMenuItem _toggleItem;
    readonly ToolStripMenuItem _recentItem;
    readonly ToolStripMenuItem _hotkeyItem;
    readonly ToolStripMenuItem _startupItem;
    AppConfig _config;
    SettingsForm? _settings;
    bool _capturing;
    int _notes;
    long _nextFieldRead;
    long _fieldSeen;
    string _fieldText = "";
    Rectangle _fieldBounds;
    FocusSnapshot? _fieldSnapshot;
    string _sentence = "";
    long _textChangedAt = Environment.TickCount64;

    // 翻译状态。_translatedFor 是已经请求过（或已翻好）的那句，避免重复请求；
    // 失败后按 3 秒、6 秒……最多 30 秒退避，不会每 60 毫秒重试一次把接口打爆。
    bool _translating;
    int _translateGeneration;
    string _translatedFor = "";
    string _doneChinese = "";
    string _failedFor = "";
    int _failCount;
    long _retryAt;
    CancellationTokenSource? _translation;
    CancellationTokenSource? _lookup;
    CancellationTokenSource? _senseRewrite;
    CancellationTokenSource? _study;
    long _wordClickAt;
    string _wordClick = "";

    public TrayApplicationContext(bool quiet = false)
    {
        StartupRegistration.Refresh();
        _config = AppConfig.Load();
        Theme.Apply(_config.Theme, _config.CardTextSize);
        _overlay.ApplyTheme();
        _ = _overlay.Handle; // 先建好窗口句柄，后台线程和键盘钩子靠它切回 UI 线程。
        _translator = new Translator(_config);
        _ime = new ImeCapture(ImeCapture.DllPath());
        _ime.Install();
        _watcher = new FieldWatcher(Environment.ProcessId);
        _hotkey = new GlobalHotkey(Dispatch);
        (_appIcon, _listeningIcon) = LoadIcons();
        _showThread = new Thread(ShowLoop) { IsBackground = true, Name = "InputTranslate.ShowEvent" };
        _showThread.Start();

        var menu = new ContextMenuStrip
        {
            Font = Theme.Ui(9F),
            Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false },
        };
        _toggleItem = new ToolStripMenuItem("开始翻译", null, (_, _) => BeginOnUi(ToggleSession))
        {
            Font = Theme.Ui(9F, FontStyle.Bold),
        };
        _recentItem = new ToolStripMenuItem("最近翻译");
        _hotkeyItem = new ToolStripMenuItem("启用热键") { CheckOnClick = true, Checked = _config.Enabled };
        _hotkeyItem.Click += (_, _) => SetHotkeyEnabled(_hotkeyItem.Checked);
        _startupItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true, Checked = StartupRegistration.IsEnabled };
        _startupItem.Click += (_, _) => ToggleStartup(_startupItem.Checked);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("显示卡片", null, (_, _) => BeginOnUi(() => ShowCard(toggle: false))));
        menu.Items.Add(_recentItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => BeginOnUi(OpenSettings)));
        menu.Items.Add(_hotkeyItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitThread()));
        foreach (ToolStripItem item in menu.Items)
            item.Padding = new Padding(item.Padding.Left, 3, item.Padding.Right, 3);
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Visible = true,
            Text = "输入翻译",
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                BeginOnUi(() => ShowCard(toggle: true));
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                BeginOnUi(ToggleSession);
        };

        _overlay.CopyRequested += (_, _) => CopyEnglish();
        _overlay.WriteRequested += (_, _) => WriteEnglish();
        _overlay.WordClicked += OnWordClicked;
        _overlay.RewriteRequested += (_, _) => _ = RewriteWordAsync();
        _overlay.AdoptRequested += (_, _) => AdoptRewrite();
        _overlay.RetryRequested += (_, _) => RetryTranslation();
        _overlay.SettingsRequested += (_, _) => OpenSettings();
        _overlay.CloseRequested += (_, _) => CloseCard();

        _hotkey.ToggleRequested += ToggleSession;
        _hotkey.EndRequested += EndSession;
        RegisterHotkey(announce: false);

        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (_capturing || _overlay.EnglishText.Length > 0)
                return;
            // 鼠标还停在卡片上就再等一会儿。
            if (_overlay.Visible && _overlay.Bounds.Contains(Cursor.Position))
            {
                _idleTimer.Start();
                return;
            }

            _overlay.Hide();
        };

        UpdateTrayState();
        TraceLog.Note("ready hotkey=" + _hotkey.HotkeyText + " ime=" + _ime.Installed + " autostart=" + quiet);
        if (!quiet)
            ShowIdle(true);
        AnnounceHotkeyProblem();
    }

    void Dispatch(Action action)
    {
        try
        {
            if (_overlay.IsHandleCreated && !_overlay.IsDisposed)
                _overlay.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The overlay is closing during shutdown.
        }
    }

    /// <summary>总是排到消息队列里异步执行，不在调用方的栈上直接跑。</summary>
    void BeginOnUi(Action action)
    {
        if (_overlay.IsHandleCreated)
            Dispatch(action);
        else
            action();
    }

    void ShowLoop()
    {
        var handles = new WaitHandle[] { _showEvent, _stopping };
        while (true)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch
            {
                return;
            }

            if (index != 0)
                return;
            Dispatch(OnSecondLaunch);
        }
    }

    /// <summary>又双击了一次 exe：听写中只把卡片调出来，不清掉内容。</summary>
    void OnSecondLaunch()
    {
        if (_capturing || _overlay.EnglishText.Length > 0)
        {
            _overlay.Show();
            return;
        }

        ShowIdle(true);
    }

    string IdleHint() => _hotkey.HotkeyRegistered
        ? "按 " + _hotkey.HotkeyText + " 开始，输入中文，按 Enter 完成"
        : _config.Enabled
            ? "热键被占用，请右键托盘图标选「开始翻译」，或在设置里换一个热键"
            : "热键已关闭，请右键托盘图标选「开始翻译」";

    void ShowIdle(bool pinTop)
    {
        _overlay.ShowIdle("输入翻译", IdleHint(), pinTop ? OverlayForm.CardPlace.TopCenter : OverlayForm.CardPlace.Stay);
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    void ShowCard(bool toggle)
    {
        if (_capturing)
        {
            _overlay.Show();
            return;
        }

        if (toggle && _overlay.Visible)
        {
            _overlay.Hide();
            return;
        }

        if (_overlay.EnglishText.Length > 0)
        {
            _overlay.Show();
            return;
        }

        ShowIdle(true);
    }

    void CloseCard()
    {
        if (_capturing)
        {
            EndSession(cancel: true);
            return;
        }

        CancelTranslation();
        CancelWordWork();
        _overlay.Hide();
    }

    void Poll()
    {
        if (!_capturing)
            return;

        var (committed, composition, _, notes, composing) = _ime.Read();
        _hotkey.Composing = composing || composition.Length > 0;
        _notes = notes;

        var now = Environment.TickCount64;
        var field = ReadField();
        var imeText = committed + (HasCjk(composition) ? composition : "");
        var shown = TerminalReader.RedactSecrets(HasCjk(field) ? field : imeText);

        // 输入框偶尔读空一下，别马上把卡片清掉。
        if (shown.Length == 0 && _sentence.Length > 0 && now - _fieldSeen < 2000)
            return;

        if (!string.Equals(shown, _sentence, StringComparison.Ordinal))
        {
            _sentence = shown;
            _textChangedAt = now;
            if (_translating && !string.Equals(shown, _translatedFor, StringComparison.Ordinal))
                CancelTranslation();
            _overlay.UpdateSource(shown, _fieldBounds);
        }

        var due = now - _textChangedAt >= _config.PauseMs || EndsClause(shown);
        var backoff = string.Equals(shown, _failedFor, StringComparison.Ordinal) && now < _retryAt;
        if (due && !_translating && !backoff && NeedsTranslation(shown))
            _ = TranslateAsync(shown);
    }

    bool NeedsTranslation(string text) =>
        ShouldTranslate(text) && !string.Equals(text, _translatedFor, StringComparison.Ordinal);

    string ReadField()
    {
        var now = Environment.TickCount64;
        if (now < _nextFieldRead)
            return _fieldText;
        _nextFieldRead = now + 120;

        var snapshot = _watcher.Latest;
        if (snapshot is not { Ok: true })
        {
            if (now - _fieldSeen > 1200)
                _fieldText = "";
            return _fieldText;
        }

        var text = FieldSentence(snapshot.Paragraph);
        if (text.Length == 0 && snapshot.NeedsOcr && now - _watcher.OcrAt < 1500)
            text = FieldSentence(_watcher.OcrText);

        if (HasCjk(text))
        {
            _fieldText = text;
            _fieldBounds = snapshot.Bounds;
            _fieldSnapshot = snapshot;
            _fieldSeen = now;
        }
        else if (now - _fieldSeen > 1200)
        {
            _fieldText = "";
        }

        return _fieldText;
    }

    static string FieldSentence(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0)
            return "";
        var lines = normalized.Split('\n');
        if (lines.Length >= 6)
            return lines[^1].Trim();
        return normalized.Length > 400 ? normalized[^400..].Trim() : normalized;
    }

    void ToggleSession()
    {
        if (_capturing)
            EndSession(cancel: false);
        else
            StartSession();
    }

    void StartSession()
    {
        if (_capturing)
            return;

        TraceLog.Note("session start");
        _capturing = true;
        _hotkey.BeginCapture();
        _sentence = "";
        _notes = 0;
        _translatedFor = "";
        _doneChinese = "";
        _failedFor = "";
        _failCount = 0;
        CancelTranslation();
        CancelWordWork();
        _ime.BeginSession();
        _watcher.SetEnabled(true);
        _fieldText = "";
        _fieldBounds = Rectangle.Empty;
        _fieldSnapshot = null;
        _fieldSeen = 0;
        _nextFieldRead = 0;
        _textChangedAt = Environment.TickCount64;
        _idleTimer.Stop();
        _overlay.BeginListening(EngineBadge(), "请开始输入中文…  Esc 取消");
        UpdateTrayState();
    }

    /// <param name="cancel">Esc / 关闭按钮：丢掉本次，隐藏卡片。回车：把这句翻完，卡片留着方便复制。</param>
    void EndSession(bool cancel)
    {
        if (!_capturing)
        {
            if (cancel)
                CloseCard();
            return;
        }

        TraceLog.Note("session end len=" + _sentence.Length + " notes=" + _notes + " cancel=" + cancel);
        _capturing = false;
        _hotkey.EndCapture();
        _ime.EndSession();
        _watcher.SetEnabled(false);
        UpdateTrayState();

        if (cancel)
        {
            CancelTranslation();
            CancelWordWork();
            _overlay.Hide();
            return;
        }

        // 以前回车会取消还没出来的翻译，打完字马上回车就什么都拿不到。
        var inFlight = _translating && string.Equals(_translatedFor, _sentence, StringComparison.Ordinal);
        if (!inFlight && ShouldTranslate(_sentence) && !string.Equals(_sentence, _doneChinese, StringComparison.Ordinal))
        {
            _failedFor = "";
            _ = TranslateAsync(_sentence);
        }

        if (!_translating && !HasCjk(_sentence) && _overlay.EnglishText.Length == 0)
        {
            _overlay.Hide();
            return;
        }

        _overlay.FinishSession();
    }

    string EngineBadge() => _config.UsesChatForTranslation
        ? _config.LevelLabel
        : _config.Engine switch
        {
            "google" => "Google",
            "mymemory" => "MyMemory",
            _ => "免费引擎",
        };

    void RetryTranslation()
    {
        var text = _overlay.SourceText.Length > 0 ? _overlay.SourceText : _sentence;
        _failedFor = "";
        _failCount = 0;
        _translatedFor = "";
        if (ShouldTranslate(text))
            _ = TranslateAsync(text);
    }

    void CancelTranslation()
    {
        _translateGeneration++;
        _translation?.Cancel();
        _translation = null;
        if (!_translating)
            return;
        _translating = false;
        _translatedFor = "";
        _overlay.SetTranslating(false);
    }

    void CancelWordWork()
    {
        _lookup?.Cancel();
        _senseRewrite?.Cancel();
        _study?.Cancel();
    }

    async Task TranslateAsync(string sentence)
    {
        CancelTranslation();
        CancelWordWork();
        var generation = ++_translateGeneration;
        var source = new CancellationTokenSource();
        _translation = source;
        var token = source.Token;
        _translating = true;
        _translatedFor = sentence;
        _overlay.SetTranslating(true);
        try
        {
            var result = await _translator.TranslateAsync(sentence, token);
            if (token.IsCancellationRequested || generation != _translateGeneration)
                return;
            if (!result.Ok)
            {
                _translatedFor = "";
                _failedFor = sentence;
                _failCount++;
                _retryAt = Environment.TickCount64 + Math.Min(30000, 3000 << Math.Min(_failCount - 1, 4));
                _overlay.ShowTranslationError(result.Detail);
                TraceLog.Note("translate fail " + TraceLog.Safe(result.Detail));
                return;
            }

            _failedFor = "";
            _failCount = 0;
            _doneChinese = sentence;
            TraceLog.Note("translate ok " + TraceLog.Safe(result.Detail) + " chars=" + result.Text.Length);
            _overlay.ShowTranslation(sentence, result.Text, result.Detail);
            AddRecent(sentence, result.Text);
            ApplyTranslation(result.Text);
            if (_config.StudyWords && _config.HasChatApi)
                _ = StartStudyAsync(sentence, result.Text);
        }
        catch (OperationCanceledException)
        {
            // A newer sentence replaced this one.
        }
        catch (Exception ex)
        {
            if (generation == _translateGeneration)
            {
                _translatedFor = "";
                _overlay.ShowTranslationError("翻译出错：" + ex.Message);
                TraceLog.Note("translate error " + TraceLog.Safe(ex.ToString()));
            }
        }
        finally
        {
            if (generation == _translateGeneration)
            {
                _translating = false;
                _overlay.SetTranslating(false);
            }

            if (_translation == source)
                _translation = null;
            source.Dispose();
        }
    }

    async Task StartStudyAsync(string source, string english)
    {
        _study?.Cancel();
        var cts = new CancellationTokenSource();
        _study = cts;
        var token = cts.Token;
        _overlay.BeginStudy();
        try
        {
            var words = await _translator.StudyAsync(english, source, token);
            if (token.IsCancellationRequested || !string.Equals(_overlay.EnglishText, english, StringComparison.Ordinal))
                return;
            if (words.Count == 0)
            {
                _overlay.EndStudy();
                return;
            }

            _overlay.ShowStudy(words);
            TraceLog.Note("study ok words=" + words.Count);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer translation.
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _overlay.EndStudy();
                TraceLog.Note("study fail " + TraceLog.Safe(ex.Message));
            }
        }
    }

    void OnWordClicked(string word)
    {
        var now = Environment.TickCount64;
        if (string.Equals(word, _wordClick, StringComparison.OrdinalIgnoreCase) && now - _wordClickAt < 350)
            return;
        _wordClick = word;
        _wordClickAt = now;

        // 再点一次同一个词就收起释义。
        if (_overlay.GlossOpen && string.Equals(_overlay.GlossWord, word, StringComparison.OrdinalIgnoreCase))
        {
            _lookup?.Cancel();
            _senseRewrite?.Cancel();
            _overlay.CloseGloss();
            return;
        }

        _ = LookupWordAsync(word);
    }

    async Task LookupWordAsync(string word)
    {
        _lookup?.Cancel();
        _senseRewrite?.Cancel();
        var cts = new CancellationTokenSource();
        _lookup = cts;
        var token = cts.Token;
        var sentence = _overlay.EnglishText;
        _overlay.BeginWordLookup(word);
        try
        {
            var gloss = await _translator.LookupWordAsync(word, sentence, token);
            if (token.IsCancellationRequested || !string.Equals(_overlay.GlossWord, word, StringComparison.OrdinalIgnoreCase))
                return;
            if (!gloss.Ok)
            {
                _overlay.ShowWordError(word, gloss.Detail.Length == 0 ? "没有查到释义" : gloss.Detail);
                TraceLog.Note("lookup fail " + TraceLog.Safe(gloss.Detail));
                return;
            }

            _overlay.ShowWordGloss(gloss);
            TraceLog.Note("lookup ok senses=" + gloss.Senses.Count);
        }
        catch (OperationCanceledException)
        {
            // Another word was clicked.
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _overlay.ShowWordError(word, ex.Message);
        }
    }

    async Task RewriteWordAsync()
    {
        var sense = _overlay.SelectedSense;
        var word = _overlay.GlossWord;
        var sentence = _overlay.EnglishText;
        if (sense == null || word.Length == 0 || sentence.Length == 0)
            return;

        _senseRewrite?.Cancel();
        var cts = new CancellationTokenSource();
        _senseRewrite = cts;
        var token = cts.Token;
        _overlay.ShowRewriteBusy();
        try
        {
            var result = await _translator.RewriteWithSenseAsync(sentence, word, sense, token);
            if (token.IsCancellationRequested || !string.Equals(_overlay.GlossWord, word, StringComparison.Ordinal))
                return;
            if (!result.Ok)
            {
                _overlay.ShowRewriteError(result.Detail.Length == 0 ? "没能改写" : result.Detail);
                return;
            }

            _overlay.ShowRewrite(result.English, result.Chinese);
        }
        catch (OperationCanceledException)
        {
            // Another sense was picked.
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _overlay.ShowRewriteError(ex.Message);
        }
    }

    void AdoptRewrite()
    {
        var text = _overlay.RewriteEnglish.Trim();
        if (text.Length == 0)
            return;
        _lookup?.Cancel();
        _senseRewrite?.Cancel();
        var chinese = _overlay.SourceText;
        _overlay.ShowTranslation(chinese, text, "按义项改写");
        AddRecent(chinese, text);
        _overlay.SetStatus("已换成这句，可以复制或写入", OverlayForm.StatusKind.Success);
    }

    void CopyEnglish()
    {
        var ok = CopyToClipboard(_overlay.EnglishText);
        _overlay.SetStatus(ok ? "已复制英文" : "复制失败，剪贴板被别的程序占用，请再点一次",
            ok ? OverlayForm.StatusKind.Success : OverlayForm.StatusKind.Error);
    }

    void WriteEnglish()
    {
        var english = _overlay.EnglishText;
        if (english.Length == 0)
            return;

        if (FocusReader.TryWrite(FreshField(), english, out var message))
        {
            _overlay.SetStatus(message, OverlayForm.StatusKind.Success);
            return;
        }

        var copied = CopyToClipboard(english);
        _overlay.SetStatus(copied ? (message.Length == 0 ? "已复制英文" : message) : "写入失败，复制也失败了，请再试一次",
            copied ? OverlayForm.StatusKind.Info : OverlayForm.StatusKind.Error);
    }

    void ApplyTranslation(string english)
    {
        if (english.Length == 0)
            return;

        if (_config.AutoWrite && FocusReader.TryWrite(FreshField(), english, out var message))
        {
            _overlay.SetStatus(message, OverlayForm.StatusKind.Success);
            return;
        }

        if (_config.AutoCopy)
            CopyEnglish();
    }

    FocusSnapshot? FreshField()
    {
        // Re-read on the UI thread right before writing: the watcher's element may be
        // stale, and UIA elements are safest to use on the thread that created them.
        try
        {
            var snapshot = FocusReader.Read(Environment.ProcessId);
            if (snapshot.Ok)
                _fieldSnapshot = snapshot;
            return snapshot.Ok ? snapshot : _fieldSnapshot;
        }
        catch
        {
            return _fieldSnapshot;
        }
    }

    static bool CopyToClipboard(string text)
    {
        if (text.Length == 0)
            return false;
        try
        {
            // 剪贴板常被别的程序短暂占着，自带重试。
            Clipboard.SetDataObject(text, true, 6, 50);
            return true;
        }
        catch
        {
            return false;
        }
    }

    void AddRecent(string chinese, string english)
    {
        if (english.Length == 0)
            return;
        _recent.RemoveAll(item => string.Equals(item.English, english, StringComparison.Ordinal));
        _recent.Insert(0, (chinese, english));
        if (_recent.Count > RecentLimit)
            _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
    }

    void ShowRecent(int index)
    {
        if (index < 0 || index >= _recent.Count)
            return;
        var (chinese, english) = _recent[index];
        if (_capturing)
            EndSession(cancel: true);
        _overlay.ShowIdle("最近翻译", "", OverlayForm.CardPlace.TopCenter);
        _overlay.ShowTranslation(chinese, english, "");
        _overlay.FinishSession();
        CopyEnglish();
    }

    void RefreshMenu()
    {
        _toggleItem.Text = _capturing ? "结束本次" : "开始翻译";
        _toggleItem.ShortcutKeyDisplayString = _hotkey.HotkeyRegistered ? _hotkey.HotkeyText : "";
        _hotkeyItem.Checked = _config.Enabled;
        _hotkeyItem.Text = _config.Enabled && _hotkey.HotkeyRegistered ? "启用热键（" + _hotkey.HotkeyText + "）" : "启用热键";
        _startupItem.Checked = StartupRegistration.IsEnabled;

        _recentItem.DropDownItems.Clear();
        _recentItem.DropDown.Font = Theme.Ui(9F);
        if (_recent.Count == 0)
        {
            _recentItem.DropDownItems.Add(new ToolStripMenuItem("还没有翻译记录（只保存在内存里）") { Enabled = false });
            return;
        }

        for (var i = 0; i < _recent.Count; i++)
        {
            var index = i;
            var (chinese, english) = _recent[i];
            var item = new ToolStripMenuItem(Shorten(chinese, 14) + "   →   " + Shorten(english, 34), null, (_, _) => BeginOnUi(() => ShowRecent(index)))
            {
                ToolTipText = english,
                Padding = new Padding(0, 3, 0, 3),
            };
            _recentItem.DropDownItems.Add(item);
        }

        _recentItem.DropDownItems.Add(new ToolStripSeparator());
        _recentItem.DropDownItems.Add(new ToolStripMenuItem("清空", null, (_, _) => _recent.Clear()));
    }

    static string Shorten(string text, int max)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            if (_settings.WindowState == FormWindowState.Minimized)
                _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
            return;
        }

        var form = new SettingsForm(_config, HotkeyStatus());
        form.Saved += ApplySettings;
        form.HotkeyEditing += editing =>
        {
            // 录新热键时先放开旧的，不然按下去会被系统热键截走，录不到。
            if (editing)
                _hotkey.Unregister();
            else
                RegisterHotkey(announce: false);
        };
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settings, form))
                _settings = null;
            RegisterHotkey(announce: false);
        };
        _settings = form;
        form.Show();
        form.Activate();
    }

    string HotkeyStatus()
    {
        if (!_config.Enabled)
            return "热键已关闭，只能从托盘菜单开始。";
        if (!_hotkey.HotkeyRegistered)
            return _hotkey.Error.Length > 0 ? _hotkey.Error : "热键没有注册成功。";
        if (_hotkey.UsingFallback)
            return _hotkey.Preferred + " 被其他程序占用，现在用的是 " + _hotkey.HotkeyText + "。";
        return "正在使用 " + _hotkey.HotkeyText + "。";
    }

    void ApplySettings(AppConfig next, bool startup)
    {
        var hotkeyChanged = !string.Equals(next.Hotkey, _config.Hotkey, StringComparison.OrdinalIgnoreCase) || next.Enabled != _config.Enabled;
        _config = next;
        if (!_config.TrySave(out var error))
            _icon.ShowBalloonTip(5000, "输入翻译", "设置没能保存：" + error, ToolTipIcon.Warning);
        _translator.UpdateConfig(_config);
        _translatedFor = "";
        _doneChinese = "";
        _failedFor = "";
        Theme.Apply(_config.Theme, _config.CardTextSize);
        _overlay.ApplyTheme();
        RegisterHotkey(announce: hotkeyChanged);
        ApplyStartup(startup);
        UpdateTrayState();
        TraceLog.Note("settings saved engine=" + _config.Engine + " level=" + _config.EnglishLevel + " chat=" + _config.HasChatApi);
    }

    void RegisterHotkey(bool announce)
    {
        if (!_config.Enabled)
        {
            _hotkey.Unregister();
            UpdateTrayState();
            return;
        }

        if (!_hotkey.HotkeyRegistered || !string.Equals(_hotkey.Preferred, _config.Hotkey, StringComparison.OrdinalIgnoreCase))
            _hotkey.Register(_config.Hotkey);
        UpdateTrayState();
        if (announce)
            AnnounceHotkeyProblem();
    }

    void AnnounceHotkeyProblem()
    {
        if (!_config.Enabled)
            return;
        if (!_hotkey.HotkeyRegistered)
            _icon.ShowBalloonTip(5000, "输入翻译", _hotkey.Error + "。请在设置里换一个热键，或右键托盘图标开始。", ToolTipIcon.Warning);
        else if (_hotkey.UsingFallback)
            _icon.ShowBalloonTip(5000, "输入翻译", _hotkey.Preferred + " 被其他程序占用，已改用 " + _hotkey.HotkeyText + "。", ToolTipIcon.Info);
    }

    void SetHotkeyEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        _ = _config.TrySave(out _);
        RegisterHotkey(announce: enabled);
    }

    void ToggleStartup(bool enabled)
    {
        if (!StartupRegistration.SetEnabled(enabled, out var error))
        {
            MessageBox.Show("设置开机自动启动失败：" + error, "输入翻译", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _startupItem.Checked = StartupRegistration.IsEnabled;
            return;
        }

        TraceLog.Note("startup=" + enabled);
        _startupItem.Checked = StartupRegistration.IsEnabled;
    }

    void ApplyStartup(bool enabled)
    {
        if (enabled == StartupRegistration.IsEnabled
            && (!enabled || StartupRegistration.PointsToCurrentExecutable))
        {
            _startupItem.Checked = StartupRegistration.IsEnabled;
            return;
        }

        ToggleStartup(enabled);
    }

    void UpdateTrayState()
    {
        _icon.Icon = _capturing ? _listeningIcon : _appIcon;
        _icon.Text = _capturing
            ? "输入翻译 · 正在听写（Enter 完成，Esc 取消）"
            : !_config.Enabled
                ? "输入翻译 · 热键已关闭"
                : _hotkey.HotkeyRegistered
                    ? "输入翻译 · " + _hotkey.HotkeyText + " 开始"
                    : "输入翻译 · 热键不可用";
    }

    bool ShouldTranslate(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2)
            return false;
        return !_config.ChineseOnly || HasCjk(trimmed);
    }

    static bool HasCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= (char)0x4E00 and <= (char)0x9FFF)
                return true;
            if (ch is >= (char)0x3400 and <= (char)0x4DBF)
                return true;
            if (ch is >= (char)0xF900 and <= (char)0xFAFF)
                return true;
        }

        return false;
    }

    static bool EndsClause(string text)
    {
        if (text.Length == 0)
            return false;
        var last = text[^1];
        return last is '。' or '！' or '？' or '，' or '、' or '；' or '.' or '!' or '?' or ',' or ';';
    }

    protected override void ExitThreadCore()
    {
        _stopping.Set();
        _showThread.Join(500);
        _timer.Stop();
        _idleTimer.Stop();
        CancelTranslation();
        CancelWordWork();
        _settings?.Close();
        _hotkey.Dispose();
        _watcher.Dispose();
        _ime.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _overlay.Dispose();
        _showEvent.Dispose();
        _stopping.Dispose();
        _appIcon.Dispose();
        _listeningIcon.Dispose();
        base.ExitThreadCore();
    }

    static (Icon Normal, Icon Listening) LoadIcons()
    {
        var size = SystemInformation.SmallIconSize;
        Icon normal;
        try
        {
            using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("InputTranslate.app.ico");
            normal = stream != null ? new Icon(stream, size) : DrawFallbackIcon(size);
        }
        catch
        {
            normal = DrawFallbackIcon(size);
        }

        return (normal, WithDot(normal, size, Color.FromArgb(34, 197, 94)));
    }

    /// <summary>听写中在托盘图标右下角加一个绿点。</summary>
    static Icon WithDot(Icon icon, Size size, Color color)
    {
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawIcon(icon, new Rectangle(0, 0, size.Width, size.Height));
            var d = Math.Max(6, size.Width * 7 / 16);
            var rect = new Rectangle(size.Width - d, size.Height - d, d - 1, d - 1);
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, Rectangle.Inflate(rect, 1, 1));
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, rect);
        }

        return FromBitmap(bitmap);
    }

    static Icon DrawFallbackIcon(Size size)
    {
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(29, 112, 214));
            graphics.FillEllipse(brush, 0, 0, size.Width - 1, size.Height - 1);
            using var font = new Font("Segoe UI", size.Height * 0.38f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(graphics, "En", font, new Rectangle(0, 0, size.Width, size.Height), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        return FromBitmap(bitmap);
    }

    // GetHicon 得到的句柄要自己 DestroyIcon，克隆一份由 Icon 自己管理。
    static Icon FromBitmap(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    sealed class MenuColors : ProfessionalColorTable
    {
        static readonly Color Hover = Color.FromArgb(230, 240, 253);
        static readonly Color Line = Color.FromArgb(222, 228, 237);

        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color MenuBorder => Line;
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Color.White;
        public override Color CheckBackground => Color.FromArgb(219, 234, 254);
        public override Color CheckSelectedBackground => Color.FromArgb(191, 219, 254);
        public override Color CheckPressedBackground => Color.FromArgb(191, 219, 254);
    }
}
