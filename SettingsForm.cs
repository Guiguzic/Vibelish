using System.Drawing;

namespace InputTranslate;

sealed class SettingsForm : Form
{
    readonly ComboBox _engine = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox _baseUrl = new();
    readonly TextBox _apiKey = new() { UseSystemPasswordChar = true };
    readonly TextBox _model = new();
    readonly NumericUpDown _pause = new() { Minimum = 300, Maximum = 3000, Increment = 100, Width = 110 };
    readonly CheckBox _chineseOnly = MakeCheck("只翻译含中文的内容");
    readonly CheckBox _autoCopy = MakeCheck("译完自动复制（会覆盖剪贴板）");
    readonly CheckBox _autoWrite = MakeCheck("译完自动写入输入框（仅当整段就是输入框内容时）");
    readonly CheckBox _startup = MakeCheck("开机时自动启动（登录 Windows 后常驻托盘）");
    readonly TableLayoutPanel _root;

    public SettingsForm(AppConfig config)
    {
        Text = "输入翻译设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        BackColor = Color.FromArgb(248, 250, 252);
        ClientSize = new Size(640, 590);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _engine.Items.AddRange(new object[] { "自动（免密钥）", "MyMemory", "Google", "OpenAI 兼容接口" });
        _engine.SelectedIndex = config.Engine switch
        {
            "mymemory" => 1,
            "google" => 2,
            "openai" => 3,
            _ => 0,
        };
        _level.Items.AddRange(new object[]
        {
            "考研（高级词汇 + 复杂句式）",
            "六级（中高级，正式书面）",
            "日常（自然简单）",
        });
        _level.SelectedIndex = config.EnglishLevel switch
        {
            "cet6" => 1,
            "plain" => 2,
            _ => 0,
        };
        _baseUrl.Text = config.OpenAiBaseUrl;
        _apiKey.Text = config.OpenAiApiKey;
        _model.Text = config.OpenAiModel;
        _pause.Value = Math.Clamp(config.PauseMs, 300, 3000);
        _chineseOnly.Checked = config.ChineseOnly;
        _autoCopy.Checked = config.AutoCopy;
        _autoWrite.Checked = config.AutoWrite;
        _startup.Checked = StartupRegistration.IsEnabled;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor,
        };
        _root = root;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Absolute RowStyles are not DPI-scaled by WinForms; scale the two fixed rows.
        var scale = DeviceDpi / 96.0;
        _root.RowStyles[0].Height = (float)(82 * scale);
        _root.RowStyles[2].Height = (float)(64 * scale);
    }

    public bool StartWithWindows => _startup.Checked;

    Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 42) };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = header.BackColor,
            Padding = new Padding(24, 10, 24, 8),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(new Label
        {
            Text = "输入翻译",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(248, 250, 252),
            Margin = new Padding(0, 0, 0, 2),
        }, 0, 0);
        stack.Controls.Add(new Label
        {
            Text = "中文输入 → 考研级英文 · 设置",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = Color.FromArgb(148, 163, 184),
            Margin = new Padding(2, 0, 0, 0),
        }, 0, 1);
        header.Controls.Add(stack);

        using var stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("InputTranslate.app.ico");
        if (stream != null)
        {
            using var icon = new Icon(stream, 44, 44);
            var picture = new PictureBox
            {
                Image = icon.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(44, 44),
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Margin = new Padding(0),
            };
            stack.Controls.Add(picture, 1, 0);
            stack.SetRowSpan(picture, 2);
        }

        return header;
    }

    Panel BuildBody()
    {
        var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 10, 24, 4), BackColor = BackColor };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = BackColor,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        AddSection(table, ref row, "翻译引擎");
        AddRow(table, ref row, "翻译引擎", _engine);
        AddRow(table, ref row, "译文难度", _level);
        AddRow(table, ref row, "接口地址", _baseUrl);
        AddRow(table, ref row, "接口密钥", _apiKey);
        AddRow(table, ref row, "模型", _model);

        AddSection(table, ref row, "使用方式");
        var pauseWrap = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
        };
        pauseWrap.Controls.Add(_pause);
        pauseWrap.Controls.Add(new Label
        {
            Text = "毫秒",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Margin = new Padding(8, 10, 0, 0),
        });
        AddRow(table, ref row, "停顿毫秒", pauseWrap);
        AddFull(table, ref row, _chineseOnly);
        AddFull(table, ref row, _autoCopy);
        AddFull(table, ref row, _autoWrite);
        AddFull(table, ref row, _startup);
        AddFull(table, ref row, BuildHint());

        wrap.Controls.Add(table);
        return wrap;
    }

    static Label BuildHint()
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = Color.FromArgb(100, 116, 139),
            Margin = new Padding(0, 10, 0, 0),
            Text = "翻译结果只显示在卡片里，原文不会保存到本机；密码框会跳过。"
                + "译文难度只对 OpenAI 兼容接口生效，MyMemory / Google 只能给出普通译文。",
        };
    }

    Panel BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(24, 15, 24, 12),
            BackColor = Color.White,
        };
        var ok = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            Width = 96,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(14, 165, 233),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = 88,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(51, 65, 85),
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Cursor = Cursors.Hand,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        AcceptButton = ok;
        CancelButton = cancel;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        footer.Controls.Add(buttons);
        return footer;
    }

    public AppConfig ReadConfig(AppConfig current)
    {
        var next = current.Clone();
        next.Engine = _engine.SelectedIndex switch
        {
            1 => "mymemory",
            2 => "google",
            3 => "openai",
            _ => "auto",
        };
        next.EnglishLevel = _level.SelectedIndex switch
        {
            1 => "cet6",
            2 => "plain",
            _ => "kaoyan",
        };
        next.OpenAiBaseUrl = _baseUrl.Text.Trim();
        next.OpenAiApiKey = _apiKey.Text.Trim();
        next.OpenAiModel = _model.Text.Trim();
        next.PauseMs = (int)_pause.Value;
        next.ChineseOnly = _chineseOnly.Checked;
        next.AutoCopy = _autoCopy.Checked;
        next.AutoWrite = _autoWrite.Checked;
        return next;
    }

    static CheckBox MakeCheck(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9.5F),
        ForeColor = Color.FromArgb(30, 41, 59),
        Margin = new Padding(0, 6, 0, 2),
    };

    static void AddSection(TableLayoutPanel table, ref int row, string title)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Margin = new Padding(0, 16, 0, 8),
        };
        table.Controls.Add(label, 0, row);
        table.SetColumnSpan(label, 2);
        row++;
    }

    static void AddRow(TableLayoutPanel table, ref int row, string name, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = name,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 8, 8, 8),
        };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 5, 0, 5);
        table.Controls.Add(label, 0, row);
        table.Controls.Add(control, 1, row);
        row++;
    }

    static void AddFull(TableLayoutPanel table, ref int row, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 3, 0, 3);
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
        row++;
    }
}
