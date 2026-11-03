using System.Diagnostics;

namespace Vibelish;

/// <summary>
/// 设置窗口：左边侧栏分页，右边是一组一组的选项。改了就生效，不用点保存。
/// </summary>
sealed class SettingsForm : Form
{
    /// <summary>常见的 OpenAI 兼容服务。模型名只是建议，可以改。</summary>
    static readonly (string Name, string Url, string Model)[] Providers =
    [
        ("自定义", "", ""),
        ("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
        ("通义千问（阿里云百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ("Kimi（月之暗面）", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ("硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct"),
        ("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
        ("Ollama（本机）", "http://localhost:11434/v1", "qwen2.5:7b"),
    ];

    static readonly string[] DirectionKeys = ["auto", "zh2en", "en2zh"];
    static readonly string[] EngineKeys = ["auto", "openai", "transmart", "mymemory", "google"];
    static readonly string[] LevelKeys = ["kaoyan", "cet6", "plain"];
    static readonly string[] ThemeKeys = ["system", "light", "dark"];
    static readonly string[] SizeKeys = ["small", "normal", "large"];
    static readonly int[] PauseValues = [350, 600, 1000];
    static readonly string[] PositionKeys = ["input", "top", "bottom"];
    static readonly int[] HideValues = [0, 5, 10, 30];
    static readonly string[] StyleKeys = ["word", "meaning", "detail"];
    static readonly string[] SpeedKeys = ["slow", "normal", "fast"];

    AppConfig _original;
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 450 };
    readonly Panel _sidebar = new();
    readonly Panel _content = new();
    readonly NavList _nav = new();
    readonly TextLabel _appName = new() { SingleLine = true, Text = "Vibelish" };
    readonly TextLabel _appVersion = new() { SingleLine = true };
    readonly PictureBox _appIcon = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
    readonly List<SettingsPage> _pages = new();

    // 通用
    readonly ToggleSwitch _enabled = new();
    readonly HotkeyBox _hotkeyBox = new();
    readonly TextField _hotkey;
    readonly ToggleSwitch _startup = new();
    SettingsRow? _enabledRow;

    // 翻译
    readonly SegmentedControl _direction = new("自动", "中译英", "英译中");
    readonly PopupButton _engine = new("自动", "只用大模型", "腾讯交互翻译", "MyMemory", "Google");
    readonly SegmentedControl _level = new("考研", "六级", "日常");
    readonly SegmentedControl _pause = new("快", "适中", "稳");
    readonly ToggleSwitch _autoCopy = new();
    readonly ToggleSwitch _autoWrite = new();
    SettingsRow? _directionRow;
    SettingsRow? _engineRow;
    SettingsRow? _levelRow;

    // 大模型
    readonly PopupButton _provider = new(Providers.Select(p => p.Name).ToArray());
    readonly TextField _baseUrl = new();
    readonly TextField _apiKey = new(null, Glyphs.View);
    readonly TextField _model = new();
    readonly ToggleSwitch _stream = new();
    readonly ToggleSwitch _fast = new();
    readonly RoundButton _test = new() { Text = "测试连接", CornerRadius = 99 };
    SettingsRow? _testRow;
    SettingsRow? _urlRow;

    // 卡片与外观
    readonly SegmentedControl _theme = new("跟随系统", "浅色", "深色");
    readonly AccentPicker _accent = new();
    readonly SegmentedControl _size = new("较小", "标准", "较大");
    readonly PopupButton _position = new("跟着输入框", "屏幕顶部", "屏幕底部");
    readonly ToggleSwitch _showSource = new();
    readonly ToggleSwitch _showHints = new();
    readonly ToggleSwitch _showEngine = new();
    readonly PopupButton _autoHide = new("不收起", "5 秒后", "10 秒后", "30 秒后");

    // 单词与朗读
    readonly ToggleSwitch _study = new();
    readonly SegmentedControl _studyStyle = new("仅单词", "带释义", "详细");
    readonly ToggleSwitch _clickLookup = new();
    readonly ToggleSwitch _showSpeak = new();
    readonly ToggleSwitch _speakOnLookup = new();
    readonly SegmentedControl _speed = new("慢", "标准", "快");
    SettingsRow? _speakRow;

    CancellationTokenSource? _testing;
    bool _loading;
    bool _syncingProvider;

    public SettingsForm(AppConfig config, string hotkeyStatus)
    {
        _original = config;
        _hotkey = new TextField(_hotkeyBox);
        Text = "Vibelish 设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Ui(9F);
        KeyPreview = true;
        DoubleBuffered = true;
        using (var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("Vibelish.app.ico"))
        {
            if (stream != null)
            {
                Icon = new Icon(stream);
                stream.Position = 0;
                using var large = new Icon(stream, 64, 64);
                _appIcon.Image = large.ToBitmap();
            }
        }

        var scale = DeviceDpi / 96.0;
        ClientSize = new Size((int)(780 * scale), (int)(580 * scale));
        _appVersion.Text = "版本 " + (typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "");

        BuildPages();
        _sidebar.Controls.AddRange(new Control[] { _appIcon, _appName, _appVersion, _nav });
        Controls.Add(_content);
        Controls.Add(_sidebar);
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);

        _loading = true;
        LoadValues(config, hotkeyStatus);
        _loading = false;
        WireEvents();
        UpdateEnabled();
        ShowPage(0);
        ApplyTheme();
    }

    /// <summary>每改一项都会触发：新配置，以及是否开机自动启动。</summary>
    public event Action<AppConfig, bool>? Saved;

    /// <summary>正在录热键（true）/ 录完了（false）。录的时候托盘要先放开旧热键。</summary>
    public event Action<bool>? HotkeyEditing;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeTheme.TitleBar(this, Theme.Current);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var scale = DeviceDpi / 96.0;
        int Sc(int value) => (int)Math.Round(value * scale);
        var sidebarWidth = Sc(212);
        _sidebar.SetBounds(0, 0, sidebarWidth, ClientSize.Height);
        _content.SetBounds(sidebarWidth, 0, Math.Max(10, ClientSize.Width - sidebarWidth), ClientSize.Height);
        _appIcon.SetBounds(Sc(20), Sc(20), Sc(34), Sc(34));
        _appName.SetBounds(_appIcon.Right + Sc(10), Sc(19), sidebarWidth - _appIcon.Right - Sc(16), _appName.Measure(200).Height);
        _appVersion.SetBounds(_appName.Left, _appName.Bottom + Sc(1), _appName.Width, _appVersion.Measure(200).Height);
        _nav.SetBounds(0, Sc(74), sidebarWidth, _nav.PreferredHeight + Sc(4));
        foreach (var page in _pages)
            page.SetBounds(0, 0, _content.Width, _content.Height);
    }

    /// <summary>主题或强调色改了：整个窗口重新上色。</summary>
    public void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Window;
        _sidebar.BackColor = p.Sidebar;
        _nav.BackColor = p.Sidebar;
        _content.BackColor = p.Window;
        _appName.Font = Theme.Ui(11F, FontStyle.Bold);
        _appName.ForeColor = p.Text;
        _appVersion.Font = Theme.Ui(8.25F);
        _appVersion.ForeColor = p.Muted;
        foreach (var field in new[] { _hotkey, _baseUrl, _apiKey, _model })
            field.ApplyTheme();
        foreach (var page in _pages)
            page.ApplyTheme();
        NativeTheme.TitleBar(this, p);
        Invalidate(true);
    }

    /// <summary>托盘菜单里改了设置（翻译方向、启用热键）：窗口里的控件跟着变，但不再触发保存。</summary>
    public void Reload(AppConfig config, string hotkeyStatus)
    {
        _debounce.Stop();
        _original = config;
        _loading = true;
        LoadValues(config, hotkeyStatus);
        _loading = false;
        UpdateEnabled();
    }

    /// <summary>托盘重新注册热键后，把结果告诉设置窗口。</summary>
    public void SetHotkeyStatus(string status)
    {
        if (_enabledRow != null)
            _enabledRow.Subtitle = status;
    }

    /// <summary>预览截图用：直接切到某一页。</summary>
    internal void ShowPage(int index)
    {
        index = Math.Max(0, Math.Min(_pages.Count - 1, index));
        if (_nav.SelectedIndex != index)
            _nav.SelectedIndex = index;
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].Visible = i == index;
        _pages[index].Arrange();
    }

    void BuildPages()
    {
        var general = AddPage(Glyphs.Settings, "通用");
        var keys = general.AddGroup("快捷键");
        _enabledRow = keys.AddRow("全局热键", "", _enabled);
        keys.AddRow("热键", "点一下再按新的组合（要带 Ctrl、Alt 或 Win），Backspace 恢复默认", _hotkey).ControlWidth = 170;
        _hotkeyBox.TextAlign = HorizontalAlignment.Center;
        var start = general.AddGroup("启动");
        start.AddRow("开机自动启动", "登录 Windows 后静默启动到托盘", _startup);
        general.AddNote("原文和译文只显示在卡片里，不写进日志；最近翻译只保存在内存里，退出就清空。密码框会自动跳过。");

        var translate = AddPage(Glyphs.Globe, "翻译");
        var engine = translate.AddGroup("");
        _directionRow = engine.AddRow("翻译方向", "", _direction);
        _engineRow = engine.AddRow("翻译引擎", "", _engine);
        _levelRow = engine.AddRow("英文难度", "只对大模型生效。中译英是译文的难度，英译中是「更地道的说法」的难度。考研：高级词汇 + 复杂句式，像经济学人", _level);
        engine.AddRow("开始翻译", "输完停顿多久开始翻译：快 0.35 秒 · 适中 0.6 秒 · 稳 1 秒。打出句号、逗号会立刻翻译", _pause);
        var after = translate.AddGroup("译完以后");
        after.AddRow("自动复制译文", "会覆盖剪贴板", _autoCopy);
        after.AddRow("自动写入输入框", "只在中译英、并且整段就是输入框全部内容时写入", _autoWrite);

        var model = AddPage(Glyphs.Bolt, "大模型");
        var api = model.AddGroup("OpenAI 兼容接口");
        api.AddRow("服务商", "", _provider);
        _urlRow = api.AddRow("接口地址", "", _baseUrl);
        api.AddRow("API Key", "", _apiKey);
        api.AddRow("模型", "", _model);
        _baseUrl.Placeholder = "https://api.deepseek.com/v1";
        _apiKey.Placeholder = "sk-…";
        _model.Placeholder = "deepseek-chat";
        _apiKey.Box.UseSystemPasswordChar = true;
        var speed = model.AddGroup("速度");
        speed.AddRow("逐字显示译文", "边生成边显示，第一个词通常一秒内就出来", _stream);
        speed.AddRow("关闭深度思考", "通义千问、智谱、硅基流动、豆包等会快很多。推理模型（如 deepseek-reasoner）关不掉，建议换成 deepseek-chat 这类", _fast);
        _test.Font = Theme.Ui(9F);
        _test.Size = new Size(Theme.Scale(this, 92), Theme.Scale(this, 28));
        _testRow = speed.AddRow("测试连接", "用上面的设置翻译一句，顺便量一下速度", _test);
        model.AddNote("密钥用 Windows 加密保存在本机，只有当前用户能解开。本机或局域网的服务（如 Ollama）可以不填密钥。");

        var card = AddPage(Glyphs.ColorPalette, "卡片与外观");
        var look = card.AddGroup("外观");
        look.AddRow("主题", "", _theme);
        look.AddRow("强调色", "", _accent);
        look.AddRow("文字大小", "", _size);
        var layout = card.AddGroup("卡片");
        layout.AddRow("卡片位置", "", _position);
        layout.AddRow("显示原文", "你输入的那句，显示在译文上面", _showSource);
        layout.AddRow("显示操作提示", "「Enter 完成 · Esc 取消」这类小字", _showHints);
        layout.AddRow("显示译文来源", "模型名或翻译引擎", _showEngine);
        layout.AddRow("完成后自动收起", "按 Enter 完成以后", _autoHide);

        var words = AddPage(Glyphs.Dictionary, "单词与朗读");
        var study = words.AddGroup("重点词");
        study.AddRow("显示重点词", "译完挑出值得记的词，和译文一起生成，不多等（需要大模型接口）", _study);
        study.AddRow("显示方式", "「详细」会把为什么值得记也写出来", _studyStyle);
        study.AddRow("点英文单词查释义", "看义项、音标、记忆点，还能按某个意思改一句", _clickLookup);
        var speak = words.AddGroup("朗读");
        _speakRow = speak.AddRow("显示朗读按钮", "", _showSpeak);
        speak.AddRow("查词时自动朗读单词", "", _speakOnLookup);
        speak.AddRow("语速", "", _speed);

        var about = AddPage(Glyphs.Info, "关于");
        var info = about.AddGroup("");
        info.AddRow("版本", "", new TextLabel { Text = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "", SingleLine = true, AlignRight = true, Size = new Size(Theme.Scale(this, 120), Theme.Scale(this, 22)) });
        info.AddRow("配置文件夹", AppConfig.Directory, LinkButton("打开", () => OpenFolder(AppConfig.Directory)));
        info.AddRow("运行日志", "只记事件和长度，不记原文", LinkButton("查看", () => OpenFile(TraceLog.FilePath)));
        about.AddNote("Vibelish：写 prompt 的时候，顺手学英语。打中文翻成考研难度的英文；直接写英文，就给出中文意思和更地道的说法。热键开始，Enter 完成，Esc 取消；点英文单词可以查释义。");
    }

    SettingsPage AddPage(string glyph, string title)
    {
        var page = new SettingsPage(title) { Visible = false };
        _pages.Add(page);
        _nav.Add(glyph, title);
        _content.Controls.Add(page);
        return page;
    }

    RoundButton LinkButton(string text, Action click)
    {
        var button = new RoundButton
        {
            Text = text,
            CornerRadius = 99,
            Font = Theme.Ui(9F),
            Size = new Size(Theme.Scale(this, 72), Theme.Scale(this, 28)),
        };
        button.Click += (_, _) => click();
        return button;
    }

    void LoadValues(AppConfig config, string hotkeyStatus)
    {
        _direction.SelectedIndex = Math.Max(0, Array.IndexOf(DirectionKeys, config.Direction));
        _engine.SelectedIndex = Math.Max(0, Array.IndexOf(EngineKeys, config.Engine));
        _level.SelectedIndex = Math.Max(0, Array.IndexOf(LevelKeys, config.EnglishLevel));
        _pause.SelectedIndex = config.PauseMs <= 450 ? 0 : config.PauseMs <= 800 ? 1 : 2;
        _autoCopy.Checked = config.AutoCopy;
        _autoWrite.Checked = config.AutoWrite;
        _baseUrl.Text = config.OpenAiBaseUrl;
        _apiKey.Text = config.OpenAiApiKey;
        _model.Text = config.OpenAiModel;
        SyncProvider();
        _stream.Checked = config.StreamOutput;
        _fast.Checked = config.FastThinking;
        _hotkeyBox.Value = HotkeySpec.TryParse(config.Hotkey, out var spec) ? spec : HotkeySpec.Parse(AppConfig.DefaultHotkey);
        _enabled.Checked = config.Enabled;
        SetHotkeyStatus(hotkeyStatus);
        _startup.Checked = StartupRegistration.IsEnabled;
        _theme.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeKeys, config.Theme));
        _accent.Selected = config.Accent;
        _size.SelectedIndex = Math.Max(0, Array.IndexOf(SizeKeys, config.CardTextSize));
        _position.SelectedIndex = Math.Max(0, Array.IndexOf(PositionKeys, config.CardPosition));
        _showSource.Checked = config.ShowSource;
        _showHints.Checked = config.ShowHints;
        _showEngine.Checked = config.ShowEngine;
        _autoHide.SelectedIndex = config.AutoHideSeconds <= 0 ? 0 : config.AutoHideSeconds <= 5 ? 1 : config.AutoHideSeconds <= 10 ? 2 : 3;
        _study.Checked = config.StudyWords;
        _studyStyle.SelectedIndex = Math.Max(0, Array.IndexOf(StyleKeys, config.StudyStyle));
        _clickLookup.Checked = config.ClickLookup;
        _showSpeak.Checked = config.ShowSpeak;
        _speakOnLookup.Checked = config.SpeakOnLookup;
        _speed.SelectedIndex = Math.Max(0, Array.IndexOf(SpeedKeys, config.SpeakSpeed));
        if (_speakRow != null)
            _speakRow.Subtitle = Speech.Available ? "用 Windows 自带的英文语音" : Speech.MissingHint;
        UpdateEngineHint();
    }

    public AppConfig ReadConfig()
    {
        var next = _original.Clone();
        next.Direction = DirectionKeys[_direction.SelectedIndex];
        next.Engine = EngineKeys[_engine.SelectedIndex];
        next.EnglishLevel = LevelKeys[_level.SelectedIndex];
        var originalPause = _original.PauseMs <= 450 ? 0 : _original.PauseMs <= 800 ? 1 : 2;
        next.PauseMs = _pause.SelectedIndex == originalPause ? _original.PauseMs : PauseValues[_pause.SelectedIndex];
        next.AutoCopy = _autoCopy.Checked;
        next.AutoWrite = _autoWrite.Checked;
        next.OpenAiBaseUrl = _baseUrl.Text.Trim();
        next.OpenAiApiKey = _apiKey.Text.Trim();
        next.OpenAiModel = _model.Text.Trim();
        next.StreamOutput = _stream.Checked;
        next.FastThinking = _fast.Checked;
        next.Hotkey = _hotkeyBox.Value.ToString();
        next.Enabled = _enabled.Checked;
        next.Theme = ThemeKeys[_theme.SelectedIndex];
        next.Accent = _accent.Selected;
        next.CardTextSize = SizeKeys[_size.SelectedIndex];
        next.CardPosition = PositionKeys[_position.SelectedIndex];
        next.ShowSource = _showSource.Checked;
        next.ShowHints = _showHints.Checked;
        next.ShowEngine = _showEngine.Checked;
        next.AutoHideSeconds = HideValues[_autoHide.SelectedIndex];
        next.StudyWords = _study.Checked;
        next.StudyStyle = StyleKeys[_studyStyle.SelectedIndex];
        next.ClickLookup = _clickLookup.Checked;
        next.ShowSpeak = _showSpeak.Checked;
        next.SpeakOnLookup = _speakOnLookup.Checked;
        next.SpeakSpeed = SpeedKeys[_speed.SelectedIndex];
        return next.Normalize();
    }

    void WireEvents()
    {
        foreach (var toggle in new[] { _enabled, _startup, _autoCopy, _autoWrite, _stream, _fast, _showSource, _showHints, _showEngine, _study, _clickLookup, _showSpeak, _speakOnLookup })
            toggle.CheckedChanged += (_, _) => Changed(immediate: true);
        foreach (var segmented in new[] { _direction, _level, _pause, _theme, _size, _studyStyle, _speed })
            segmented.SelectedIndexChanged += (_, _) => Changed(immediate: true);
        foreach (var popup in new[] { _engine, _position, _autoHide })
            popup.SelectedIndexChanged += (_, _) => Changed(immediate: true);
        _accent.SelectedChanged += (_, _) => Changed(immediate: true);
        _provider.SelectedIndexChanged += (_, _) => ApplyProvider();
        _baseUrl.TextChanged += (_, _) =>
        {
            SyncProvider();
            Changed(immediate: false);
        };
        _apiKey.TextChanged += (_, _) => Changed(immediate: false);
        _model.TextChanged += (_, _) => Changed(immediate: false);
        _apiKey.TrailingClicked += (_, _) => _apiKey.Box.UseSystemPasswordChar = !_apiKey.Box.UseSystemPasswordChar;
        _hotkeyBox.Enter += (_, _) => HotkeyEditing?.Invoke(true);
        _hotkeyBox.Leave += (_, _) => HotkeyEditing?.Invoke(false);
        _hotkeyBox.Changed += () => Changed(immediate: true);
        _test.Click += async (_, _) => await TestAsync();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Commit();
        };
    }

    void Changed(bool immediate)
    {
        if (_loading)
            return;
        UpdateEnabled();
        UpdateEngineHint();
        _debounce.Stop();
        if (immediate)
            Commit();
        else
            _debounce.Start();
    }

    void Commit()
    {
        if (_loading || IsDisposed)
            return;
        var url = _baseUrl.Text.Trim();
        var badUrl = url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"));
        if (_urlRow != null)
        {
            _urlRow.Subtitle = badUrl ? "要以 http:// 或 https:// 开头" : "";
            _urlRow.SubtitleColor = Theme.Current.Error;
        }

        Saved?.Invoke(ReadConfig(), _startup.Checked);
    }

    void UpdateEngineHint()
    {
        if (_directionRow != null)
        {
            _directionRow.Subtitle = DirectionKeys[_direction.SelectedIndex] switch
            {
                "zh2en" => "输入中文，翻成英文",
                "en2zh" => "直接用英文写，卡片显示中文意思；配了大模型还会给出更地道的说法",
                _ => "有中文就翻成英文；纯英文就翻成中文，配了大模型还会给出更地道的说法",
            };
        }

        if (_engineRow == null)
            return;
        var key = EngineKeys[_engine.SelectedIndex];
        var chat = _baseUrl.Text.Trim().Length > 0 && _model.Text.Trim().Length > 0
            && (_apiKey.Text.Trim().Length > 0 || AppConfig.IsLocalEndpoint(_baseUrl.Text));
        _engineRow.Subtitle = key switch
        {
            "auto" => chat ? "现在会用大模型；出错时自动改用免费引擎" : "还没配置大模型，现在用免费引擎（腾讯交互翻译，慢了同时问 MyMemory / Google）",
            "openai" => chat ? "只用大模型，出错不换引擎" : "还没填好大模型接口，翻译会失败",
            "transmart" => "国内访问快，质量接近机器翻译平均水平",
            "mymemory" => "每天有免费额度，速度一般",
            _ => "国内网络经常连不上",
        };
        _engineRow.SubtitleColor = key == "openai" && !chat ? Theme.Current.Error : Theme.Current.Muted;
    }

    void UpdateEnabled()
    {
        var key = EngineKeys[_engine.SelectedIndex];
        _level.Enabled = key is "auto" or "openai";
        _hotkey.Enabled = _enabled.Checked;
        _studyStyle.Enabled = _study.Checked;
        _speakOnLookup.Enabled = Speech.Available;
        _speed.Enabled = Speech.Available;
        _showSpeak.Enabled = Speech.Available;
    }

    async Task TestAsync()
    {
        _testing?.Cancel();
        var probe = ReadConfig();
        if (probe.OpenAiBaseUrl.Length == 0 || probe.OpenAiModel.Length == 0)
        {
            ShowTest("先填接口地址和模型", Theme.Current.Error);
            return;
        }

        if (!probe.HasChatApi)
        {
            ShowTest("还缺 API Key（只有本机 / 局域网的服务可以不填）", Theme.Current.Error);
            return;
        }

        var testing = new CancellationTokenSource();
        _testing = testing;
        _test.Enabled = false;
        ShowTest("正在连接…", Theme.Current.Muted);
        try
        {
            var result = await Translator.TestChatAsync(probe, testing.Token);
            if (testing.IsCancellationRequested || IsDisposed)
                return;
            if (!result.Ok)
            {
                ShowTest(result.Detail, Theme.Current.Error);
                return;
            }

            var speed = result.FirstMs > 0
                ? "第一个词 " + Seconds(result.FirstMs) + "，全部 " + Seconds(result.TotalMs)
                : "用时 " + Seconds(result.TotalMs);
            ShowTest("连接正常 · " + speed + " · " + result.Text, Theme.Current.Success);
        }
        catch (OperationCanceledException)
        {
            // Closed or restarted.
        }
        finally
        {
            if (!IsDisposed)
                _test.Enabled = true;
        }
    }

    static string Seconds(long ms) => (ms / 1000.0).ToString("0.0#") + " 秒";

    void ShowTest(string text, Color color)
    {
        if (_testRow == null)
            return;
        _testRow.Subtitle = text;
        _testRow.SubtitleColor = color;
        _pages[_nav.SelectedIndex].Arrange();
    }

    void ApplyProvider()
    {
        if (_syncingProvider || _provider.SelectedIndex <= 0)
            return;
        var preset = Providers[_provider.SelectedIndex];
        _syncingProvider = true;
        _baseUrl.Text = preset.Url;
        var model = _model.Text.Trim();
        if (model.Length == 0 || Providers.Any(p => p.Model.Length > 0 && p.Model == model))
            _model.Text = preset.Model;
        _syncingProvider = false;
        Changed(immediate: true);
    }

    void SyncProvider()
    {
        if (_syncingProvider)
            return;
        var url = _baseUrl.Text.Trim().TrimEnd('/');
        var index = 0;
        for (var i = 1; i < Providers.Length; i++)
        {
            if (string.Equals(Providers[i].Url, url, StringComparison.OrdinalIgnoreCase))
                index = i;
        }

        _syncingProvider = true;
        _provider.SelectedIndex = index;
        _syncingProvider = false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && !_hotkeyBox.Focused)
        {
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关窗前把还没提交的输入框改动交上去。
        if (_debounce.Enabled)
        {
            _debounce.Stop();
            Commit();
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _testing?.Cancel();
        _debounce.Dispose();
        base.OnFormClosed(e);
    }

    static void OpenFolder(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch
        {
            // Ignore.
        }
    }

    static void OpenFile(string file)
    {
        try
        {
            if (File.Exists(file))
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            else
                OpenFolder(Path.GetDirectoryName(file)!);
        }
        catch
        {
            // Ignore.
        }
    }

    /// <summary>一页设置：大标题，下面是若干分组和说明文字，放不下时可以滚动。</summary>
    sealed class SettingsPage : Panel
    {
        readonly TextLabel _title = new() { SingleLine = true };
        readonly List<Control> _items = new();
        readonly Dictionary<SettingsGroup, TextLabel> _captions = new();
        bool _arranging;

        public SettingsPage(string title)
        {
            AutoScroll = true;
            DoubleBuffered = true;
            _title.Text = title;
            Controls.Add(_title);
        }

        public SettingsGroup AddGroup(string caption)
        {
            var group = new SettingsGroup();
            if (caption.Length > 0)
            {
                var label = new TextLabel { Text = caption, SingleLine = true };
                _captions[group] = label;
                Controls.Add(label);
            }

            _items.Add(group);
            Controls.Add(group);
            return group;
        }

        public TextLabel AddNote(string text)
        {
            var note = new TextLabel { Text = text };
            _items.Add(note);
            Controls.Add(note);
            return note;
        }

        public void ApplyTheme()
        {
            var p = Theme.Current;
            BackColor = p.Window;
            _title.Font = Theme.Ui(15F, FontStyle.Bold);
            _title.ForeColor = p.Text;
            foreach (var caption in _captions.Values)
            {
                caption.Font = Theme.Ui(8.5F, FontStyle.Bold);
                caption.ForeColor = p.Muted;
            }

            foreach (var item in _items)
            {
                if (item is SettingsGroup group)
                {
                    group.ApplyTheme();
                }
                else if (item is TextLabel note)
                {
                    note.Font = Theme.Ui(8.25F);
                    note.ForeColor = p.Muted;
                }
            }

            NativeTheme.Apply(this, p.Dark);
            Arrange();
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeTheme.Apply(this, Theme.Current.Dark);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Arrange();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            // 某一行的说明文字变了（比如测试连接的结果），行高可能跟着变。
            if (Visible)
                Arrange();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
                Arrange();
        }

        /// <summary>从上往下排：标题、分组、说明。</summary>
        public void Arrange()
        {
            if (_arranging || Width <= 0)
                return;
            _arranging = true;
            try
            {
                var scale = DeviceDpi / 96.0;
                int Sc(int value) => (int)Math.Round(value * scale);
                var offset = AutoScrollPosition;
                var padX = Sc(28);
                var width = Math.Max(Sc(200), ClientSize.Width - padX * 2);
                var y = Sc(22);
                var titleHeight = _title.Measure(width).Height;
                _title.SetBounds(padX + offset.X, y + offset.Y, width, titleHeight);
                y += titleHeight + Sc(14);
                foreach (var item in _items)
                {
                    if (item is SettingsGroup group)
                    {
                        if (_captions.TryGetValue(group, out var caption))
                        {
                            var h = caption.Measure(width).Height;
                            caption.SetBounds(padX + Sc(14) + offset.X, y + offset.Y, width - Sc(14), h);
                            y += h + Sc(6);
                        }

                        var height = group.Arrange(width);
                        group.SetBounds(padX + offset.X, y + offset.Y, width, height);
                        y += height + Sc(20);
                    }
                    else if (item is TextLabel note)
                    {
                        var h = note.Measure(width - Sc(28)).Height;
                        note.SetBounds(padX + Sc(14) + offset.X, y - Sc(10) + offset.Y, width - Sc(28), h);
                        y += h + Sc(10);
                    }
                }

                AutoScrollMinSize = new Size(0, y + Sc(8));
                foreach (var item in _items)
                    item.Invalidate(true);
            }
            finally
            {
                _arranging = false;
            }
        }
    }

    /// <summary>录热键的输入框：直接按下想要的组合。</summary>
    sealed class HotkeyBox : TextBox
    {
        HotkeySpec _value;

        public HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
        }

        public event Action? Changed;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public HotkeySpec Value
        {
            get => _value;
            set
            {
                _value = value;
                Text = value.ToString();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            var modifiers = keyData & Keys.Modifiers;
            if (key == Keys.Tab && (modifiers & ~Keys.Shift) == Keys.None)
                return base.ProcessCmdKey(ref msg, keyData);
            if (key is Keys.Back or Keys.Delete && modifiers == Keys.None)
            {
                Value = HotkeySpec.Parse(AppConfig.DefaultHotkey);
                Changed?.Invoke();
                return true;
            }

            if (HotkeySpec.IsModifierKey(key))
            {
                Text = Describe(modifiers) + "…";
                return true;
            }

            var spec = HotkeySpec.FromKeys(modifiers, key);
            if (!spec.IsValid)
            {
                Text = "要带 Ctrl 或 Alt";
                return true;
            }

            Value = spec;
            Changed?.Invoke();
            return true;
        }

        protected override void OnLeave(EventArgs e)
        {
            Text = _value.ToString();
            base.OnLeave(e);
        }

        static string Describe(Keys modifiers)
        {
            var parts = new List<string>();
            if ((modifiers & Keys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & Keys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & Keys.Shift) != 0) parts.Add("Shift");
            return string.Join("+", parts) + (parts.Count > 0 ? "+" : "");
        }
    }
}
