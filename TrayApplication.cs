using System.Drawing.Drawing2D;

namespace InputTranslate;

sealed class TrayApplicationContext : ApplicationContext
{
    readonly OverlayForm _overlay = new();
    readonly NotifyIcon _icon;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 9000 };
    readonly Icon _appIcon;
    readonly Translator _translator;
    readonly ImeCapture _ime;
    readonly GlobalHotkey _hotkey = new();
    readonly FieldWatcher _watcher;
    readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, @"Local\InputTranslate.Show");
    readonly Thread _showThread;
    AppConfig _config;
    ToolStripMenuItem _listenItem;
    ToolStripMenuItem _startupItem;
    ToolStripMenuItem _toggleItem;
    bool _syncingStartup;
    bool _capturing;
    int _notes;
    long _nextFieldRead;
    long _fieldSeen;
    string _fieldText = "";
    Rectangle _fieldBounds;
    FocusSnapshot? _fieldSnapshot;
    bool _translating;
    string _sentence = "";
    string _translatedFor = "";
    CancellationTokenSource? _translation;
    CancellationTokenSource? _lookup;
    CancellationTokenSource? _senseRewrite;
    CancellationTokenSource? _study;
    long _wordClickAt;
    string _wordClick = "";
    string _pending = "";
    long _textChangedAt = Environment.TickCount64;

    public TrayApplicationContext(bool quiet = false)
    {
        StartupRegistration.Refresh();
        _config = AppConfig.Load();
        _translator = new Translator(_config);
        _ime = new ImeCapture(ImeCapture.DllPath());
        _ime.Install();
        _watcher = new FieldWatcher(Environment.ProcessId);
        _appIcon = LoadAppIcon();
        _showThread = new Thread(ShowLoop) { IsBackground = true, Name = "InputTranslate.ShowEvent" };
        _showThread.Start();

        _listenItem = new ToolStripMenuItem("允许热键") { Checked = _config.Enabled, CheckOnClick = true };
        _listenItem.CheckedChanged += (_, _) => SetEnabled(_listenItem.Checked);
        _startupItem = new ToolStripMenuItem("开机自动启动")
        {
            Checked = StartupRegistration.IsEnabled,
            CheckOnClick = true,
        };
        _startupItem.CheckedChanged += (_, _) => ToggleStartup(_startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_listenItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        _toggleItem = new ToolStripMenuItem("开始 / 结束") { ShortcutKeyDisplayString = "" };
        _toggleItem.Click += (_, _) => BeginOnUi(ToggleSession);
        menu.Items.Add(_toggleItem);
        menu.Items.Add("显示卡片", null, (_, _) => BeginOnUi(() => ShowIdle(true)));
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Visible = true,
            Text = "输入翻译",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => BeginOnUi(ToggleSession);

        _overlay.CopyRequested += (_, _) => CopyEnglish();
        _overlay.WriteRequested += (_, _) => WriteEnglish();
        _overlay.WordClicked += OnWordClicked;
        _overlay.RewriteRequested += (_, _) => _ = RewriteWordAsync();
        _overlay.AdoptRequested += (_, _) => AdoptRewrite();

        _hotkey.ToggleRequested += () => BeginOnUi(ToggleSession);
        _hotkey.EndRequested += () => BeginOnUi(EndSession);
        _hotkey.Install();
        _toggleItem.Text = "开始 / 结束（" + HotkeyLabel + "）";
        if (!_config.Enabled)
            _hotkey.SetEnabled(false);

        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (!_capturing && _overlay.EnglishText.Length == 0)
                _overlay.Hide();
        };

        UpdateIconText();
        TraceLog.Note("ready hotkey=" + _hotkey.HotkeyRegistered + " hook=" + _hotkey.HookInstalled + " ime=" + _ime.Installed + " autostart=" + quiet);
        if (!quiet)
        {
            ShowIdle(true);
            var tip = BuildStartTip();
            _icon.ShowBalloonTip(4000, "输入翻译", tip, _hotkey.HotkeyRegistered ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
    }

    string HotkeyLabel => _hotkey.HotkeyText.Length > 0 ? _hotkey.HotkeyText : "托盘菜单";

    const string LevelLabel = "中文 → 英文";

    void ShowLoop()
    {
        while (true)
        {
            try
            {
                if (!_showEvent.WaitOne(2000))
                    continue;
            }
            catch
            {
                return;
            }

            for (var i = 0; i < 50 && !_overlay.IsHandleCreated; i++)
                Thread.Sleep(100);
            if (!_overlay.IsHandleCreated)
                continue;

            try
            {
                _overlay.BeginInvoke(() => ShowIdle(true));
            }
            catch
            {
                // The overlay may be closing during shutdown.
            }
        }
    }

    string BuildStartTip()
    {
        if (!_hotkey.HotkeyRegistered)
            return "热键不可用：" + _hotkey.Error + "。请用托盘菜单开始。";
        var hint = "按 " + HotkeyLabel + " 开始，输入中文，回车结束。";
        if (!_ime.Installed)
            hint += "（输入法钩子未加载，只能读取支持辅助功能的输入框）";
        return hint;
    }

    void Poll()
    {
        if (!_capturing)
            return;

        var (committed, composition, _, notes, composing) = _ime.Read();
        _hotkey.Composing = composing || composition.Length > 0;
        _notes = notes;

        var field = ReadField();
        var imeText = committed + (HasCjk(composition) ? composition : "");
        var shown = TerminalReader.RedactSecrets(HasCjk(field) ? field : imeText);

        if (shown.Length == 0
            && _sentence.Length > 0
            && Environment.TickCount64 - _fieldSeen < 2000)
        {
            return;
        }

        if (!string.Equals(shown, _sentence, StringComparison.Ordinal))
        {
            _sentence = shown;
            var fresh = !string.Equals(shown, _translatedFor, StringComparison.Ordinal);
            if (fresh)
                _translation?.Cancel();
            var english = fresh ? "" : _overlay.EnglishText;
            var place = shown.Length == 0 ? OverlayForm.CardPlace.Stay : OverlayForm.CardPlace.AboveInput;
            _overlay.ShowSession(shown, english, shown.Length == 0 ? "请开始输入中文…" : "", place, _fieldBounds);
        }

        var due = Environment.TickCount64 - _changedAt(shown) >= _config.PauseMs || EndsClause(shown);
        if (ShouldTranslate(shown)
            && due
            && !_translating
            && !string.Equals(shown, _translatedFor, StringComparison.Ordinal))
        {
            _ = TranslateAsync(shown);
        }
    }

    long _changedAt(string text)
    {
        if (!string.Equals(text, _pending, StringComparison.Ordinal))
        {
            _pending = text;
            _textChangedAt = Environment.TickCount64;
        }

        return _textChangedAt;
    }

    void ShowIdle(bool pinTop)
    {
        _overlay.SetBadge("");
        var hint = "按 " + HotkeyLabel + " 开始";
        _overlay.ShowSession("", "", hint, pinTop ? OverlayForm.CardPlace.TopCenter : OverlayForm.CardPlace.Stay);
        if (pinTop)
        {
            TraceLog.Note("card " + _overlay.Bounds.X + "," + _overlay.Bounds.Y + " " + _overlay.Bounds.Width + "x" + _overlay.Bounds.Height + " visible=" + _overlay.Visible);
            _idleTimer.Start();
        }
    }

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
            EndSession();
        else
            StartSession();
    }

    void StartSession()
    {
        if (!_config.Enabled)
            return;

        TraceLog.Note("session start");
        _capturing = true;
        _hotkey.Capturing = true;
        _hotkey.Composing = false;
        _sentence = "";
        _pending = "";
        _notes = 0;
        _translatedFor = "";
        _translation?.Cancel();
        _ime.BeginSession();
        _watcher.SetEnabled(true);
        _fieldText = "";
        _fieldBounds = Rectangle.Empty;
        _fieldSnapshot = null;
        _fieldSeen = 0;
        _nextFieldRead = 0;
        _idleTimer.Stop();
        _overlay.SetBadge(LevelLabel);
        _overlay.ShowSession("", "", "请开始输入中文…", OverlayForm.CardPlace.Cursor);
        UpdateIconText();
    }

    void EndSession()
    {
        if (!_capturing)
        {
            if (_overlay.Visible)
                _overlay.Hide();
            return;
        }

        TraceLog.Note("session end len=" + _sentence.Length + " notes=" + _notes);
        _capturing = false;
        _hotkey.Capturing = false;
        _hotkey.Composing = false;
        _ime.EndSession();
        _watcher.SetEnabled(false);
        _translation?.Cancel();

        if (!HasCjk(_sentence) && _overlay.EnglishText.Length == 0)
            _overlay.Hide();
        else
            _overlay.ShowSession(_sentence, _overlay.EnglishText, "", OverlayForm.CardPlace.Stay);
        UpdateIconText();
    }

    async Task TranslateAsync(string sentence)
    {
        _translating = true;
        _translatedFor = sentence;
        _lookup?.Cancel();
        _senseRewrite?.Cancel();
        _study?.Cancel();
        _translation?.Cancel();
        _translation = new CancellationTokenSource();
        var token = _translation.Token;
        _overlay.SetStatus("正在翻译…");
        try
        {
            var result = await _translator.TranslateAsync(sentence, token);
            if (token.IsCancellationRequested || !string.Equals(_translatedFor, sentence, StringComparison.Ordinal))
                return;
            if (!result.Ok)
            {
                _overlay.ShowEnglish("", result.Detail);
                _translatedFor = "";
                TraceLog.Note("translate fail " + TraceLog.Safe(result.Detail));
                return;
            }

            TraceLog.Note("translate ok " + result.Detail + " chars=" + result.Text.Length);
            _overlay.ShowEnglish(result.Text, result.Detail);
            ApplyTranslation(result.Text);
            _ = StartStudyAsync(sentence, result.Text);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _translating = false;
        }
    }

    async Task StartStudyAsync(string source, string english)
    {
        _study?.Cancel();
        _study = new CancellationTokenSource();
        var token = _study.Token;
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
            return;
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
        _ = LookupWordAsync(word);
    }

    async Task LookupWordAsync(string word)
    {
        _lookup?.Cancel();
        _senseRewrite?.Cancel();
        _lookup = new CancellationTokenSource();
        var token = _lookup.Token;
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
            TraceLog.Note("lookup ok " + word + " senses=" + gloss.Senses.Count);
        }
        catch (OperationCanceledException)
        {
            return;
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
        _senseRewrite = new CancellationTokenSource();
        var token = _senseRewrite.Token;
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
            return;
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
        _overlay.ShowEnglish(text, "");
        _overlay.SetStatus("已换成这句，可以复制或写入");
    }

    void CopyEnglish()
    {
        var ok = CopyToClipboard(_overlay.EnglishText);
        _overlay.SetStatus(ok ? "已复制英文" : "复制失败，请再点一次");
    }

    void WriteEnglish()
    {
        var english = _overlay.EnglishText;
        if (english.Length == 0)
            return;

        if (FocusReader.TryWrite(FreshField(), english, out var message))
        {
            _overlay.SetStatus(message);
            return;
        }

        _ = CopyToClipboard(english);
        _overlay.SetStatus(message.Length == 0 ? "已复制英文" : message);
    }

    void ApplyTranslation(string english)
    {
        if (english.Length == 0)
            return;

        if (_config.AutoWrite && FocusReader.TryWrite(FreshField(), english, out var message))
        {
            _overlay.SetStatus(message);
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
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            Thread.Sleep(40);
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    void OpenSettings()
    {
        using var form = new SettingsForm(_config);
        if (form.ShowDialog() != DialogResult.OK)
            return;
        _config = form.ReadConfig(_config);
        _config.Save();
        _translator.UpdateConfig(_config);
        _translatedFor = "";
        SetEnabled(_config.Enabled);
        ApplyStartup(form.StartWithWindows);
    }

    void SetEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        _config.Save();
        _listenItem.Checked = enabled;
        _hotkey.SetEnabled(enabled);
        if (!enabled)
            EndSession();
        UpdateIconText();
    }

    void ToggleStartup(bool enabled)
    {
        if (_syncingStartup)
            return;

        if (!StartupRegistration.SetEnabled(enabled, out var error))
        {
            MessageBox.Show("设置开机自动启动失败：" + error, "输入翻译", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SyncStartupItem();
            return;
        }

        TraceLog.Note("startup=" + enabled);
        SyncStartupItem();
    }

    void ApplyStartup(bool enabled)
    {
        if (enabled == StartupRegistration.IsEnabled
            && (!enabled || StartupRegistration.PointsToCurrentExecutable))
        {
            SyncStartupItem();
            return;
        }

        ToggleStartup(enabled);
    }

    void SyncStartupItem()
    {
        _syncingStartup = true;
        _startupItem.Checked = StartupRegistration.IsEnabled;
        _syncingStartup = false;
    }

    void UpdateIconText()
    {
        _icon.Text = !_config.Enabled
            ? "输入翻译：已关闭"
            : _capturing
                ? "输入翻译：正在听写"
                : "输入翻译：" + HotkeyLabel;
    }

    void BeginOnUi(Action action)
    {
        if (_overlay.IsHandleCreated && _overlay.InvokeRequired)
            _overlay.BeginInvoke(action);
        else
            action();
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
        return last is '。' or '！' or '？' or '，' or '、' or '.' or '!' or '?' or ',';
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _idleTimer.Stop();
        _translation?.Cancel();
        _showEvent.Set();
        _showThread.Join(300);
        _hotkey.Dispose();
        _watcher.Dispose();
        _ime.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _overlay.Dispose();
        _showEvent.Dispose();
        _appIcon.Dispose();
        base.ExitThreadCore();
    }

    static Icon LoadAppIcon()
    {
        try
        {
            var stream = typeof(TrayApplicationContext).Assembly
                .GetManifestResourceStream("InputTranslate.app.ico");
            if (stream != null)
                return new Icon(stream);
        }
        catch
        {
            // Fall back to the drawn icon below.
        }

        return Icon.FromHandle(CreateIcon());
    }

    static IntPtr CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(14, 116, 178));
        graphics.FillEllipse(brush, 1, 1, 30, 30);
        using var font = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        graphics.DrawString("En", font, Brushes.White, 5, 9);
        return bitmap.GetHicon();
    }
}
