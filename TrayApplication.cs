using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Vibelish;

sealed class TrayApplicationContext : ApplicationContext
{
    const int RecentLimit = 8;

    readonly OverlayForm _overlay = new();
    readonly NotifyIcon _icon;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 9000 };
    readonly System.Windows.Forms.Timer _hideTimer = new();
    readonly ContextMenuStrip _menu;
    readonly Icon _appIcon;
    readonly Icon _listeningIcon;
    readonly Translator _translator;
    readonly ImeCapture _ime;
    readonly GlobalHotkey _hotkey;
    readonly FieldWatcher _watcher;
    readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, @"Local\Vibelish.Show");
    readonly ManualResetEvent _stopping = new(false);
    readonly Thread _showThread;
    readonly List<(string Source, string Result, bool ToNative, string? Better)> _recent = new();
    readonly ToolStripMenuItem _toggleItem;
    readonly ToolStripMenuItem _recentItem;
    readonly ToolStripMenuItem _directionItem;
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

    // 开始听写时输入框里已经有的外语（终端里的英文占位提示、以前写的草稿）不翻，改过以后才翻。
    string _baseline = "";
    bool _baselineOpen;
    long _sessionStartedAt;

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
        Theme.Apply(_config.Theme, _config.CardTextSize, _config.Accent);
        _overlay.ApplyOptions(_config);
        _overlay.ApplyTheme();
        _ = _overlay.Handle; // 先建好窗口句柄，后台线程和键盘钩子靠它切回 UI 线程。
        _translator = new Translator(_config);
        _ime = new ImeCapture(ImeCapture.DllPath());
        _ime.Install();
        _watcher = new FieldWatcher(Environment.ProcessId);
        _hotkey = new GlobalHotkey(Dispatch);
        (_appIcon, _listeningIcon) = LoadIcons();
        _showThread = new Thread(ShowLoop) { IsBackground = true, Name = "Vibelish.ShowEvent" };
        _showThread.Start();

        var menu = ThemedMenu.Create();
        _menu = menu;
        _toggleItem = new ToolStripMenuItem("开始翻译", null, (_, _) => BeginOnUi(ToggleSession))
        {
            Font = Theme.Ui(9F, FontStyle.Bold),
        };
        _recentItem = new ToolStripMenuItem("最近翻译");
        _directionItem = new ToolStripMenuItem("翻译方向");
        // 文字跟着母语和在学的语言变，打开菜单时再填（RefreshMenu）。
        foreach (var key in new[] { "auto", "forward", "reverse" })
            _directionItem.DropDownItems.Add(new ToolStripMenuItem(key, null, (_, _) => BeginOnUi(() => SetDirection(key))) { Tag = key });

        _hotkeyItem = new ToolStripMenuItem("启用热键") { CheckOnClick = true, Checked = _config.Enabled };
        _hotkeyItem.Click += (_, _) => SetHotkeyEnabled(_hotkeyItem.Checked);
        _startupItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true, Checked = StartupRegistration.IsEnabled };
        _startupItem.Click += (_, _) => ToggleStartup(_startupItem.Checked);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("显示卡片", null, (_, _) => BeginOnUi(() => ShowCard(toggle: false))));
        menu.Items.Add(_recentItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_directionItem);
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => BeginOnUi(OpenSettings)));
        menu.Items.Add(_hotkeyItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitThread()));
        menu.Opening += (_, _) =>
        {
            RefreshMenu();
            ThemeMenu();
        };

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Visible = true,
            Text = "Vibelish",
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

        _overlay.CopyRequested += (_, _) => CopyResult();
        _overlay.WriteRequested += (_, _) => WriteResult();
        _overlay.SpeakRequested += (_, _) => Speak(_overlay.SpeakText);
        _overlay.SpeakWordRequested += word => Speak(word);
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
            if (_capturing || _overlay.ResultText.Length > 0)
                return;
            // 鼠标还停在卡片上就再等一会儿。
            if (_overlay.Visible && _overlay.Bounds.Contains(Cursor.Position))
            {
                _idleTimer.Start();
                return;
            }

            _overlay.Hide();
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_capturing || _translating)
                return;
            // 鼠标停在卡片上、或者正在看释义，就再等一轮。
            if (_overlay.PointerOver || _overlay.GlossOpen)
            {
                _hideTimer.Start();
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
        if (_capturing || _overlay.ResultText.Length > 0)
        {
            _overlay.Show();
            return;
        }

        ShowIdle(true);
    }

    /// <summary>提示里说「输入中文」「输入英文」还是「输入中文或英文」。</summary>
    string InputLanguage => _config.Direction switch
    {
        "forward" => _config.Native.Name,
        "reverse" => _config.Target.Name,
        _ => _config.Native.Name + "或" + _config.Target.Name,
    };

    string IdleHint() => _hotkey.HotkeyRegistered
        ? "按 " + _hotkey.HotkeyText + " 开始，输入" + InputLanguage + "，按 Enter 完成"
        : _config.Enabled
            ? "热键被占用，请右键托盘图标选「开始翻译」，或在设置里换一个热键"
            : "热键已关闭，请右键托盘图标选「开始翻译」";

    void ShowIdle(bool pinTop)
    {
        _overlay.ShowIdle("Vibelish", IdleHint(), pinTop ? OverlayForm.CardPlace.TopCenter : OverlayForm.CardPlace.Stay);
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

        if (_overlay.ResultText.Length > 0)
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
        var imeText = committed + (composition.Any(char.IsLetter) ? composition : "");
        var shown = TerminalReader.RedactSecrets(_config.Readable(field) ? field : imeText);
        if (_baselineOpen)
        {
            if (shown.Length > 0)
            {
                _baseline = shown;
                _baselineOpen = false;
            }
            else if (now - _sessionStartedAt > 1500)
            {
                _baselineOpen = false;
            }
        }

        if (_baseline.Length > 0 && string.Equals(shown, _baseline, StringComparison.Ordinal) && _config.ToNative(shown))
            shown = "";

        // 输入框偶尔读空一下，别马上把卡片清掉。
        if (shown.Length == 0 && _sentence.Length > 0 && now - _fieldSeen < 2000)
            return;

        if (!string.Equals(shown, _sentence, StringComparison.Ordinal))
        {
            _sentence = shown;
            _textChangedAt = now;
            if (_translating && !string.Equals(shown, _translatedFor, StringComparison.Ordinal))
                CancelTranslation();
            _overlay.UpdateSource(shown, _fieldBounds, shown.Length > 0 && _config.ToNative(shown));
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

        if (_config.Readable(text))
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
        _sessionStartedAt = _textChangedAt;
        _baseline = "";
        _baselineOpen = true;
        _idleTimer.Stop();
        _hideTimer.Stop();
        _overlay.BeginListening(Badge(null), "请开始输入" + InputLanguage + "…  Esc 取消");
        UpdateTrayState();
        // 趁用户还在打字，先把到翻译服务的连接建好，第一句就不用等握手。
        _translator.WarmUp();
    }

    /// <summary>按 Enter 完成以后，过几秒自动收起卡片（设置里可关）。</summary>
    void ScheduleAutoHide()
    {
        _hideTimer.Stop();
        if (_config.AutoHideSeconds <= 0)
            return;
        _hideTimer.Interval = _config.AutoHideSeconds * 1000;
        _hideTimer.Start();
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

        if (!_translating && !ShouldTranslate(_sentence) && _overlay.ResultText.Length == 0)
        {
            _overlay.Hide();
            return;
        }

        _overlay.FinishSession();
        ScheduleAutoHide();
    }

    /// <summary>卡片标题后面的小字：翻译方向 · 难度（没用大模型时是引擎名）。还不知道方向时传 null。</summary>
    string Badge(bool? toNative)
    {
        var direction = toNative is { } back ? _config.ArrowLabel(back) : _config.DirectionLabel;
        var engine = _config.UsesChatForTranslation
            ? _config.LevelLabel
            : _config.Engine switch
            {
                "google" => "Google",
                "mymemory" => "MyMemory",
                "transmart" => "腾讯交互翻译",
                _ => "免费引擎",
            };
        return direction + " · " + engine;
    }

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
        var toNative = _config.ToNative(sentence);
        _overlay.SetBadge(Badge(toNative));
        _overlay.SetTranslating(true);
        // 逐字输出：后台线程收到一段就排到 UI 线程，过期的（已经换了一句）直接丢掉。
        Action<string>? partial = null;
        if (_config.StreamOutput)
        {
            partial = text => Dispatch(() =>
            {
                if (!token.IsCancellationRequested && generation == _translateGeneration && _translating)
                    _overlay.ShowPartial(sentence, text, toNative);
            });
        }

        try
        {
            var result = await _translator.TranslateAsync(sentence, token, partial);
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
            TraceLog.Note("translate ok " + TraceLog.Safe(result.Detail) + " chars=" + result.Text.Length
                + " first=" + result.FirstMs + "ms total=" + result.TotalMs + "ms");
            _overlay.ShowTranslation(sentence, result.Text, result.Detail, result.ToNative, result.Better);
            AddRecent(sentence, result.Text, result.ToNative, result.Better);
            ApplyTranslation(result.ToNative);
            if (result.Words is { Count: > 0 } words)
                _overlay.ShowStudy(words); // 和译文同一次请求带回来的，不用再等。
            else if (_config.StudyWords && _config.HasChatApi)
                _ = StartStudyAsync(result.ToNative ? _overlay.LookupSentence : result.Text, result.ToNative ? result.Text : sentence);
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

    async Task StartStudyAsync(string english, string chinese)
    {
        _study?.Cancel();
        var cts = new CancellationTokenSource();
        _study = cts;
        var token = cts.Token;
        var shown = _overlay.ResultText;
        _overlay.BeginStudy();
        try
        {
            var words = await _translator.StudyAsync(english, chinese, token);
            if (token.IsCancellationRequested || !string.Equals(_overlay.ResultText, shown, StringComparison.Ordinal))
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
        var sentence = _overlay.LookupSentence;
        _overlay.BeginWordLookup(word);
        if (_config.SpeakOnLookup)
            Speech.Speak(word, _config.SpeakSpeed, _config.Target.Culture);
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
        var sentence = _overlay.LookupSentence;
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
        if (_overlay.ToNative)
        {
            // 反向：换掉的是「更地道的说法」，母语的意思不变。
            _overlay.AdoptBetter(text);
            _overlay.SetStatus("已换成这句，可以朗读或替换到输入框", OverlayForm.StatusKind.Success);
            return;
        }

        var chinese = _overlay.SourceText;
        _overlay.ShowTranslation(chinese, text, "按义项改写");
        AddRecent(chinese, text, false, null);
        _overlay.SetStatus("已换成这句，可以复制或写入", OverlayForm.StatusKind.Success);
    }

    /// <summary>朗读的总是在学的语言（正向的译文，或反向的更地道说法 / 原文）。</summary>
    void Speak(string text)
    {
        if (text.Trim().Length == 0)
            return;
        if (!Speech.Speak(text, _config.SpeakSpeed, _config.Target.Culture))
            _overlay.SetStatus(Speech.MissingHint(_config.Target.Name), OverlayForm.StatusKind.Error);
    }

    void CopyResult()
    {
        var ok = CopyToClipboard(_overlay.ResultText);
        var language = _overlay.ToNative ? _config.Native : _config.Target;
        _overlay.SetStatus(ok ? "已复制" + language.Name : "复制失败，剪贴板被别的程序占用，请再点一次",
            ok ? OverlayForm.StatusKind.Success : OverlayForm.StatusKind.Error);
    }

    /// <summary>正向写译文；反向写更地道的说法，替换掉自己写的那句。</summary>
    void WriteResult()
    {
        var text = _overlay.WriteText;
        if (text.Length == 0)
            return;

        if (FocusReader.TryWrite(FreshField(), text, _config.Target.Name, out var message))
        {
            _overlay.SetStatus(message, OverlayForm.StatusKind.Success);
            return;
        }

        var copied = CopyToClipboard(text);
        _overlay.SetStatus(copied ? (message.Length == 0 ? "已复制" + _config.Target.Name : message) : "写入失败，复制也失败了，请再试一次",
            copied ? OverlayForm.StatusKind.Info : OverlayForm.StatusKind.Error);
    }

    void ApplyTranslation(bool toNative)
    {
        var text = _overlay.ResultText;
        if (text.Length == 0)
            return;

        // 反向不自动写入：母语写进输入框没用，悄悄换掉自己写的外语也太突然。
        if (_config.AutoWrite && !toNative && FocusReader.TryWrite(FreshField(), text, _config.Target.Name, out var message))
        {
            _overlay.SetStatus(message, OverlayForm.StatusKind.Success);
            return;
        }

        if (_config.AutoCopy)
            CopyResult();
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

    void AddRecent(string source, string result, bool toNative, string? better)
    {
        if (result.Length == 0)
            return;
        _recent.RemoveAll(item => string.Equals(item.Result, result, StringComparison.Ordinal));
        _recent.Insert(0, (source, result, toNative, better));
        if (_recent.Count > RecentLimit)
            _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
    }

    void ShowRecent(int index)
    {
        if (index < 0 || index >= _recent.Count)
            return;
        var (source, result, toNative, better) = _recent[index];
        if (_capturing)
            EndSession(cancel: true);
        _overlay.ShowIdle("最近翻译", "", OverlayForm.CardPlace.TopCenter);
        _overlay.ShowTranslation(source, result, "", toNative, better);
        _overlay.FinishSession();
        CopyResult();
        ScheduleAutoHide();
    }

    /// <summary>托盘菜单里换翻译方向：和在设置里改一样，立刻生效并保存。</summary>
    void SetDirection(string direction)
    {
        if (_config.Direction == direction)
            return;
        var next = _config.Clone();
        next.Direction = direction;
        ApplySettings(next.Normalize(), StartupRegistration.IsEnabled);
        _settings?.Reload(_config, HotkeyStatus());
    }

    /// <summary>托盘菜单跟着主题和强调色走。</summary>
    void ThemeMenu()
    {
        var p = Theme.Current;
        _menu.Renderer = new ToolStripProfessionalRenderer(new ThemedMenu.MenuColors(p)) { RoundedEdges = false };
        _menu.BackColor = p.Group;
        void Paint(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor = item.Enabled ? p.Text : p.Dim;
                if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
                {
                    parent.DropDown.BackColor = p.Group;
                    Paint(parent.DropDownItems);
                }
            }
        }

        Paint(_menu.Items);
    }

    void RefreshMenu()
    {
        _toggleItem.Text = _capturing ? "结束本次" : "开始翻译";
        _toggleItem.ShortcutKeyDisplayString = _hotkey.HotkeyRegistered ? _hotkey.HotkeyText : "";
        _hotkeyItem.Checked = _config.Enabled;
        _hotkeyItem.Text = _config.Enabled && _hotkey.HotkeyRegistered ? "启用热键（" + _hotkey.HotkeyText + "）" : "启用热键";
        _startupItem.Checked = StartupRegistration.IsEnabled;
        var native = _config.Native;
        var target = _config.Target;
        foreach (ToolStripMenuItem item in _directionItem.DropDownItems)
        {
            item.Checked = Equals(item.Tag, _config.Direction);
            item.Text = item.Tag switch
            {
                "forward" => AppConfig.PairLabel(native, target) + "（" + native.Name + " → " + target.Name + "）",
                "reverse" => AppConfig.PairLabel(target, native) + "（" + target.Name + " → " + native.Name + "）",
                _ => "自动（看输入的是" + native.Name + "还是" + target.Name + "）",
            };
        }

        _directionItem.Text = "翻译方向（" + _config.DirectionLabel + "）";

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
            var (source, result, toNative, _) = _recent[i];
            // 用空格分词的语言（英文这些）占地方，多留几个字。
            var (from, to) = toNative ? (target, native) : (native, target);
            var label = Shorten(source, from.Spaced ? 30 : 14) + "   →   " + Shorten(result, to.Spaced ? 34 : 18);
            var item = new ToolStripMenuItem(label, null, (_, _) => BeginOnUi(() => ShowRecent(index)))
            {
                ToolTipText = result,
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
        var lookChanged = next.Theme != _config.Theme || next.Accent != _config.Accent || next.CardTextSize != _config.CardTextSize;
        _config = next;
        if (!_config.TrySave(out var error))
            _icon.ShowBalloonTip(5000, "Vibelish", "设置没能保存：" + error, ToolTipIcon.Warning);
        _translator.UpdateConfig(_config);
        _translatedFor = "";
        _doneChinese = "";
        _failedFor = "";
        Theme.Apply(_config.Theme, _config.CardTextSize, _config.Accent);
        _overlay.ApplyOptions(_config);
        if (lookChanged)
        {
            _overlay.ApplyTheme();
            _settings?.ApplyTheme(); // 设置窗口自己也立刻换颜色。
        }

        if (_config.AutoHideSeconds <= 0)
            _hideTimer.Stop();
        if (_capturing)
            _overlay.SetBadge(Badge(null));
        RegisterHotkey(announce: hotkeyChanged);
        if (hotkeyChanged)
            _settings?.SetHotkeyStatus(HotkeyStatus());
        ApplyStartup(startup);
        UpdateTrayState();
        TraceLog.Note("settings saved engine=" + _config.Engine + " level=" + _config.EnglishLevel + " dir=" + _config.Direction + " chat=" + _config.HasChatApi);
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
            _icon.ShowBalloonTip(5000, "Vibelish", _hotkey.Error + "。请在设置里换一个热键，或右键托盘图标开始。", ToolTipIcon.Warning);
        else if (_hotkey.UsingFallback)
            _icon.ShowBalloonTip(5000, "Vibelish", _hotkey.Preferred + " 被其他程序占用，已改用 " + _hotkey.HotkeyText + "。", ToolTipIcon.Info);
    }

    void SetHotkeyEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        _ = _config.TrySave(out _);
        RegisterHotkey(announce: enabled);
        // 设置窗口开着的话也要同步，不然它下次保存会把这个开关改回去。
        _settings?.Reload(_config, HotkeyStatus());
    }

    void ToggleStartup(bool enabled)
    {
        if (!StartupRegistration.SetEnabled(enabled, out var error))
        {
            MessageBox.Show("设置开机自动启动失败：" + error, "Vibelish", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            ? "Vibelish · 正在听写（Enter 完成，Esc 取消）"
            : !_config.Enabled
                ? "Vibelish · 热键已关闭"
                : _hotkey.HotkeyRegistered
                    ? "Vibelish · " + _hotkey.HotkeyText + " 开始"
                    : "Vibelish · 热键不可用";
    }

    bool ShouldTranslate(string text) => _config.Translatable(text);

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
        _hideTimer.Stop();
        CancelTranslation();
        CancelWordWork();
        Speech.Dispose();
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
            using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("Vibelish.app.ico");
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
}
