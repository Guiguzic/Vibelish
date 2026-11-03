using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace InputTranslate;

sealed class SettingsForm : Form
{
    static readonly Color Page = Color.FromArgb(243, 246, 250);
    static readonly Color CardBack = Color.White;
    static readonly Color CardLine = Color.FromArgb(222, 228, 237);
    static readonly Color Ink = Color.FromArgb(17, 24, 39);
    static readonly Color LabelInk = Color.FromArgb(55, 65, 81);
    static readonly Color Hint = Color.FromArgb(107, 114, 128);
    static readonly Color Good = Color.FromArgb(22, 128, 61);
    static readonly Color Bad = Color.FromArgb(200, 30, 30);
    static readonly Color Accent = Color.FromArgb(29, 112, 214);

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

    static readonly string[] EngineKeys = ["auto", "openai", "mymemory", "google"];
    static readonly string[] LevelKeys = ["kaoyan", "cet6", "plain"];
    static readonly string[] ThemeKeys = ["system", "dark", "light"];
    static readonly string[] SizeKeys = ["small", "normal", "large"];

    readonly AppConfig _original;
    readonly ComboBox _engine = Combo("自动（有大模型接口就用，失败时用免费引擎）", "只用大模型接口", "MyMemory（免费）", "Google（免费，国内可能连不上）");
    readonly ComboBox _level = Combo("考研（高级词汇 + 复杂句式）", "六级（中高级，正式书面）", "日常（自然简单）");
    readonly ComboBox _provider = Combo(Providers.Select(p => p.Name).ToArray());
    readonly TextBox _baseUrl = new();
    readonly TextBox _apiKey = new() { UseSystemPasswordChar = true };
    readonly TextBox _model = new();
    readonly CheckBox _showKey = Check("显示");
    readonly RoundButton _test = new();
    readonly Label _testResult = HintLabel("");
    readonly HotkeyBox _hotkey = new();
    readonly Label _hotkeyStatus = HintLabel("");
    readonly NumericUpDown _pause = new() { Minimum = 300, Maximum = 3000, Increment = 100, Width = 90, TextAlign = HorizontalAlignment.Right };
    readonly CheckBox _chineseOnly = Check("只翻译含中文的内容");
    readonly CheckBox _study = Check("译完自动挑出值得记的词（需要大模型接口）");
    readonly CheckBox _autoCopy = Check("译完自动复制英文（会覆盖剪贴板）");
    readonly CheckBox _autoWrite = Check("译完自动写入输入框（仅当整段就是输入框的全部内容）");
    readonly CheckBox _enabled = Check("启用全局热键");
    readonly CheckBox _startup = Check("开机自动启动（登录 Windows 后常驻托盘）");
    readonly ComboBox _theme = Combo("跟随系统", "深色", "浅色");
    readonly ComboBox _size = Combo("较小", "标准", "较大");
    readonly ToolTip _tips = new();
    readonly List<SettingsSection> _sections = new();
    Panel? _scroll;
    Label? _privacy;
    CancellationTokenSource? _testing;
    bool _syncingProvider;

    public SettingsForm(AppConfig config, string hotkeyStatus)
    {
        _original = config;
        Text = "输入翻译 设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Ui(9F);
        BackColor = Page;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 700);
        KeyPreview = true;
        using (var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("InputTranslate.app.ico"))
        {
            if (stream != null)
                Icon = new Icon(stream);
        }

        LoadValues(config, hotkeyStatus);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Page, Margin = new Padding(0) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);

        _provider.SelectedIndexChanged += (_, _) => ApplyProvider();
        _baseUrl.TextChanged += (_, _) => SyncProvider();
        _showKey.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !_showKey.Checked;
        _engine.SelectedIndexChanged += (_, _) => UpdateEnabled();
        _enabled.CheckedChanged += (_, _) => UpdateEnabled();
        _hotkey.Enter += (_, _) => HotkeyEditing?.Invoke(true);
        _hotkey.Leave += (_, _) => HotkeyEditing?.Invoke(false);
        _hotkey.Changed += () => _hotkeyStatus.Text = "保存后生效。";
        _test.Click += async (_, _) => await TestAsync();
        UpdateEnabled();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitLayout();
    }

    /// <summary>表格布局按百分比列量换行文字时会多算高度，留出一大块空白。
    /// 窗口缩放完以后按实际宽度定死标签列和说明文字的最大宽度。</summary>
    void FitLayout()
    {
        if (_scroll == null)
            return;
        var scale = DeviceDpi / 96.0;
        var width = _scroll.ClientSize.Width - _scroll.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        foreach (var section in _sections)
            section.Fit(width, scale);
        if (_privacy != null)
            _privacy.MaximumSize = new Size(Math.Max(100, width - (int)(8 * scale)), 0);
        _scroll.PerformLayout();
    }

    /// <summary>预览截图用：把设置页滚到底。</summary>
    internal void ScrollToEnd()
    {
        if (_scroll != null)
            _scroll.AutoScrollPosition = new Point(0, _scroll.DisplayRectangle.Height);
    }

    /// <summary>点了保存：新配置，以及是否开机自动启动。</summary>
    public event Action<AppConfig, bool>? Saved;

    /// <summary>正在录热键（true）/ 录完了（false）。录的时候托盘要先放开旧热键。</summary>
    public event Action<bool>? HotkeyEditing;

    void LoadValues(AppConfig config, string hotkeyStatus)
    {
        _engine.SelectedIndex = Math.Max(0, Array.IndexOf(EngineKeys, config.Engine));
        _level.SelectedIndex = Math.Max(0, Array.IndexOf(LevelKeys, config.EnglishLevel));
        _baseUrl.Text = config.OpenAiBaseUrl;
        _apiKey.Text = config.OpenAiApiKey;
        _model.Text = config.OpenAiModel;
        SyncProvider();
        _hotkey.Value = HotkeySpec.TryParse(config.Hotkey, out var spec) ? spec : HotkeySpec.Parse(AppConfig.DefaultHotkey);
        _hotkeyStatus.Text = hotkeyStatus;
        _pause.Value = Math.Clamp(config.PauseMs, 300, 3000);
        _chineseOnly.Checked = config.ChineseOnly;
        _study.Checked = config.StudyWords;
        _autoCopy.Checked = config.AutoCopy;
        _autoWrite.Checked = config.AutoWrite;
        _enabled.Checked = config.Enabled;
        _startup.Checked = StartupRegistration.IsEnabled;
        _theme.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeKeys, config.Theme));
        _size.SelectedIndex = Math.Max(0, Array.IndexOf(SizeKeys, config.CardTextSize));
    }

    public AppConfig ReadConfig()
    {
        var next = _original.Clone();
        next.Engine = EngineKeys[Math.Max(0, _engine.SelectedIndex)];
        next.EnglishLevel = LevelKeys[Math.Max(0, _level.SelectedIndex)];
        next.OpenAiBaseUrl = _baseUrl.Text.Trim();
        next.OpenAiApiKey = _apiKey.Text.Trim();
        next.OpenAiModel = _model.Text.Trim();
        next.Hotkey = _hotkey.Value.ToString();
        next.PauseMs = (int)_pause.Value;
        next.ChineseOnly = _chineseOnly.Checked;
        next.StudyWords = _study.Checked;
        next.AutoCopy = _autoCopy.Checked;
        next.AutoWrite = _autoWrite.Checked;
        next.Enabled = _enabled.Checked;
        next.Theme = ThemeKeys[Math.Max(0, _theme.SelectedIndex)];
        next.CardTextSize = SizeKeys[Math.Max(0, _size.SelectedIndex)];
        return next.Normalize();
    }

    Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.FromArgb(22, 27, 38),
            Padding = new Padding(24, 16, 24, 14),
            Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        using (var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("InputTranslate.app.ico"))
        {
            if (stream != null)
            {
                using var icon = new Icon(stream, 48, 48);
                var picture = new PictureBox
                {
                    Image = icon.ToBitmap(),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(42, 42),
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 2, 14, 0),
                };
                header.Controls.Add(picture, 0, 0);
                header.SetRowSpan(picture, 2);
            }
        }

        header.Controls.Add(new Label
        {
            Text = "输入翻译 设置",
            AutoSize = true,
            Font = Theme.Ui(15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(243, 246, 250),
            Margin = new Padding(0, 0, 0, 2),
        }, 1, 0);
        var version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "";
        header.Controls.Add(new Label
        {
            Text = "版本 " + version + " · 中文输入，按考研难度翻成英文",
            AutoSize = true,
            Font = Theme.Ui(8.5F),
            ForeColor = Color.FromArgb(148, 163, 184),
            Margin = new Padding(1, 0, 0, 0),
        }, 1, 1);
        return header;
    }

    Control BuildBody()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Page, Padding = new Padding(20, 14, 20, 8) };
        _scroll = scroll;
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Page,
            Margin = new Padding(0),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var translate = new SettingsSection("翻译");
        translate.AddRow("翻译引擎", _engine);
        translate.AddHint("自动：配置了大模型接口就用它；接口失败或没配置时，改用免费的 MyMemory / Google。");
        translate.AddRow("译文难度", _level);
        translate.AddHint("只对大模型接口生效。考研会先忠实直译，再按考研阅读的词汇和句式润色。");
        translate.AddFull(_study);
        translate.AddFull(_chineseOnly);

        var api = new SettingsSection("大模型接口（OpenAI 兼容）");
        api.AddRow("服务商", _provider);
        api.AddRow("接口地址", _baseUrl);
        api.AddRow("接口密钥", KeyRow());
        api.AddHint("密钥用 Windows 加密保存在本机，只有当前用户能解开。本机或局域网的服务可以不填密钥。");
        api.AddRow("模型", _model);
        _test.Text = "测试连接";
        _test.Variant = ButtonVariant.Secondary;
        _test.PaletteOverride = Theme.LightPalette;
        _test.Font = Theme.Ui(9F);
        _test.Size = new Size(96, 30);
        _test.Margin = new Padding(0, 4, 12, 4);
        var testRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
        _testResult.Margin = new Padding(0, 10, 0, 0);
        _testResult.MaximumSize = new Size(300, 0);
        testRow.Controls.Add(_test);
        testRow.Controls.Add(_testResult);
        api.AddRow("", testRow);

        var usage = new SettingsSection("使用");
        usage.AddFull(_enabled);
        usage.AddRow("热键", HotkeyRow());
        usage.AddHint("点一下热键框，再按新的组合（要带 Ctrl 或 Alt），Backspace 恢复默认。");
        usage.AddHint(_hotkeyStatus);
        var pauseRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
        pauseRow.Controls.Add(_pause);
        pauseRow.Controls.Add(new Label { Text = "毫秒没有新输入就开始翻译", AutoSize = true, ForeColor = Hint, Margin = new Padding(8, 6, 0, 0), BackColor = Color.Transparent });
        usage.AddRow("停顿", pauseRow);
        usage.AddFull(_autoCopy);
        usage.AddFull(_autoWrite);
        usage.AddFull(_startup);

        var look = new SettingsSection("外观");
        look.AddRow("卡片主题", _theme);
        look.AddRow("文字大小", _size);

        var row = 0;
        _sections.AddRange(new[] { translate, api, usage, look });
        foreach (var section in _sections)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.Margin = new Padding(0, 0, 0, 12);
            section.Dock = DockStyle.Fill;
            stack.Controls.Add(section, 0, row++);
        }

        var privacy = HintLabel("原文和译文只显示在卡片里，不写进日志；最近翻译只保存在内存里，退出就清空。密码框会自动跳过。");
        privacy.Margin = new Padding(4, 0, 4, 8);
        _privacy = privacy;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(privacy, 0, row);

        scroll.Controls.Add(stack);
        return scroll;
    }

    Control KeyRow()
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Color.Transparent };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _apiKey.Dock = DockStyle.Fill;
        _apiKey.Margin = new Padding(0, 3, 8, 3);
        _showKey.Margin = new Padding(0, 6, 0, 0);
        panel.Controls.Add(_apiKey, 0, 0);
        panel.Controls.Add(_showKey, 1, 0);
        return panel;
    }

    Control HotkeyRow()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
        _hotkey.Width = 180;
        _hotkey.Margin = new Padding(0, 3, 8, 3);
        var reset = new RoundButton
        {
            Text = "恢复默认",
            PaletteOverride = Theme.LightPalette,
            Font = Theme.Ui(9F),
            Size = new Size(84, 28),
            Margin = new Padding(0, 2, 0, 2),
        };
        reset.Click += (_, _) =>
        {
            _hotkey.Value = HotkeySpec.Parse(AppConfig.DefaultHotkey);
            _hotkeyStatus.Text = "保存后生效。";
        };
        panel.Controls.Add(_hotkey);
        panel.Controls.Add(reset);
        return panel;
    }

    Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.White,
            Padding = new Padding(20, 12, 20, 12),
            Margin = new Padding(0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(CardLine);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0), BackColor = Color.White };
        links.Controls.Add(Link("打开配置文件夹", () => OpenFolder(AppConfig.Directory)));
        links.Controls.Add(Link("查看日志", () => OpenFile(Path.Combine(AppConfig.Directory, "run.log"))));

        var cancel = new RoundButton
        {
            Text = "取消",
            PaletteOverride = Theme.LightPalette,
            Font = Theme.Ui(9.5F),
            Size = new Size(88, 34),
            Margin = new Padding(0, 0, 10, 0),
            DialogResult = DialogResult.Cancel,
        };
        var save = new RoundButton
        {
            Text = "保存",
            Variant = ButtonVariant.Primary,
            PaletteOverride = Theme.LightPalette,
            Font = Theme.Ui(9.5F, FontStyle.Bold),
            Size = new Size(96, 34),
            Margin = new Padding(0),
        };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        CancelButton = cancel;

        footer.Controls.Add(links, 0, 0);
        footer.Controls.Add(cancel, 2, 0);
        footer.Controls.Add(save, 3, 0);
        return footer;
    }

    void Save()
    {
        var url = _baseUrl.Text.Trim();
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")))
        {
            MessageBox.Show(this, "接口地址要以 http:// 或 https:// 开头，例如 https://api.deepseek.com/v1", "输入翻译", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _baseUrl.Focus();
            return;
        }

        var next = ReadConfig();
        if (next.Engine == "openai" && !next.HasChatApi)
        {
            var answer = MessageBox.Show(this, "选了「只用大模型接口」，但接口地址、密钥或模型还没填全，翻译会失败。\n\n仍然保存吗？",
                "输入翻译", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
        }

        Saved?.Invoke(next, _startup.Checked);
        Close();
    }

    async Task TestAsync()
    {
        _testing?.Cancel();
        var probe = ReadConfig();
        if (probe.OpenAiBaseUrl.Length == 0 || probe.OpenAiModel.Length == 0)
        {
            ShowTest("先填接口地址和模型。", Bad);
            return;
        }

        if (!probe.HasChatApi)
        {
            ShowTest("还缺接口密钥（只有本机 / 局域网的服务可以不填）。", Bad);
            return;
        }

        var testing = new CancellationTokenSource();
        _testing = testing;
        _test.Enabled = false;
        ShowTest("正在连接…", Hint);
        try
        {
            var result = await Translator.TestChatAsync(probe, testing.Token);
            if (testing.IsCancellationRequested || IsDisposed)
                return;
            ShowTest(result.Ok ? "连接成功：" + result.Text : result.Detail, result.Ok ? Good : Bad);
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

    void ShowTest(string text, Color color)
    {
        _testResult.ForeColor = color;
        _testResult.Text = text;
        _tips.SetToolTip(_testResult, text);
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
        ShowTest("", Hint);
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

    void UpdateEnabled()
    {
        _level.Enabled = EngineKeys[Math.Max(0, _engine.SelectedIndex)] is "auto" or "openai";
        _hotkey.Enabled = _enabled.Checked;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _testing?.Cancel();
        _tips.Dispose();
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

    static LinkLabel Link(string text, Action click)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkColor = Accent,
            ActiveLinkColor = Color.FromArgb(20, 84, 166),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 0, 16, 0),
            BackColor = Color.White,
        };
        link.LinkClicked += (_, _) => click();
        return link;
    }

    static ComboBox Combo(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.System };
        combo.Items.AddRange(items);
        return combo;
    }

    static CheckBox Check(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Ink,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 4, 0, 4),
        UseMnemonic = false,
    };

    static Label HintLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Ui(8.5F),
        ForeColor = Hint,
        BackColor = Color.Transparent,
        MaximumSize = new Size(440, 0),
        UseMnemonic = false,
    };

    /// <summary>白底圆角的一组设置：左列标签，右列控件。</summary>
    sealed class SettingsSection : TableLayoutPanel
    {
        readonly List<Label> _hints = new();
        int _row;

        public SettingsSection(string title)
        {
            ColumnCount = 2;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Page;
            Padding = new Padding(18, 14, 18, 12);
            DoubleBuffered = true;
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var heading = new Label
            {
                Text = title,
                AutoSize = true,
                Font = Theme.Ui(10.5F, FontStyle.Bold),
                ForeColor = Ink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 8),
                UseMnemonic = false,
            };
            Add(heading, 0, span: 2);
        }

        public void AddRow(string name, Control control)
        {
            var label = new Label
            {
                Text = name,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = LabelInk,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 6, 8, 6),
                UseMnemonic = false,
            };
            if (control is TextBox or ComboBox)
                control.Dock = DockStyle.Fill;
            if (control.Margin == DefaultMargin)
                control.Margin = new Padding(0, 4, 0, 4);
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(label, 0, _row);
            Controls.Add(control, 1, _row);
            _row++;
        }

        public void AddHint(string text) => AddHint(HintLabel(text));

        /// <summary>按实际宽度定死标签列和说明文字的换行宽度。</summary>
        public void Fit(int width, double scale)
        {
            var labelWidth = (int)Math.Round(104 * scale);
            ColumnStyles[0].Width = labelWidth;
            var room = Math.Max(100, width - Padding.Horizontal - labelWidth - (int)Math.Round(4 * scale));
            foreach (var hint in _hints)
                hint.MaximumSize = new Size(room, 0);
        }

        public void AddHint(Label hint)
        {
            _hints.Add(hint);
            hint.Margin = new Padding(0, 0, 0, 6);
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(hint, 1, _row);
            _row++;
        }

        public void AddFull(Control control) => Add(control, 0, span: 2);

        void Add(Control control, int column, int span)
        {
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(control, column, _row);
            SetColumnSpan(control, span);
            _row++;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Page);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Theme.RoundedRect(bounds, (int)Math.Round(10 * DeviceDpi / 96.0));
            using var fill = new SolidBrush(CardBack);
            g.FillPath(fill, path);
            using var pen = new Pen(CardLine);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>录热键的输入框：直接按下想要的组合。</summary>
    sealed class HotkeyBox : TextBox
    {
        HotkeySpec _value;

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = Color.White;
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
