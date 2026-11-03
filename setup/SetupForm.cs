using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using Vibelish;

namespace VibelishSetup;

sealed class SetupForm : Form
{
    static readonly Color Page = Color.FromArgb(243, 246, 250);
    static readonly Color CardBack = Color.White;
    static readonly Color CardLine = Color.FromArgb(222, 228, 237);
    static readonly Color Ink = Color.FromArgb(17, 24, 39);
    static readonly Color Body = Color.FromArgb(55, 65, 81);
    static readonly Color Hint = Color.FromArgb(107, 114, 128);
    static readonly Color Bad = Color.FromArgb(200, 30, 30);
    static readonly Color Accent = Color.FromArgb(29, 112, 214);
    static readonly Color Good = Color.FromArgb(22, 163, 74);

    readonly string _installed = InstallerCore.InstalledVersion;
    readonly CheckBox _startup;
    readonly CheckBox _desktop;
    readonly CheckBox _launch;
    readonly TextLabel _status = new() { SingleLine = true, ForeColor = Hint, Font = Theme.Ui(9F) };
    readonly ProgressLine _progress = new();
    readonly TextLabel _error = new() { ForeColor = Bad, Font = Theme.Ui(9F), Visible = false };
    readonly RoundButton _install = new() { Variant = ButtonVariant.Primary, PaletteOverride = Theme.LightPalette, CornerRadius = 8 };
    readonly RoundButton _cancel = new() { Variant = ButtonVariant.Secondary, PaletteOverride = Theme.LightPalette, CornerRadius = 8 };
    readonly Panel _options;
    readonly Panel _done;
    readonly TextLabel _doneTitle = new() { SingleLine = true, ForeColor = Ink, Font = Theme.Ui(15F, FontStyle.Bold) };
    readonly TextLabel _doneSubtitle = new() { ForeColor = Hint, Font = Theme.Ui(9F) };
    readonly List<TextLabel> _wrapped = new();
    readonly List<Card> _cards = new();
    readonly Panel _body;
    bool _installing;
    bool _finished;
    bool _previewOnly;

    bool IsUpdate => _installed.Length > 0;

    public SetupForm()
    {
        Text = "Vibelish 安装程序";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Ui(9F);
        BackColor = Page;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 480);

        using (var iconStream = typeof(SetupForm).Assembly.GetManifestResourceStream("VibelishSetup.app.ico"))
        {
            if (iconStream != null)
                Icon = new Icon(iconStream);
        }

        // 更新时沿用原来的开机启动和桌面快捷方式选择。
        _startup = Check("开机自动启动（推荐）", IsUpdate ? InstallerCore.StartupEnabled : true);
        _desktop = Check("在桌面创建快捷方式", IsUpdate && InstallerCore.DesktopShortcutExists);
        _launch = Check("现在启动 Vibelish", true);

        _options = BuildOptions();
        _done = BuildDone();
        _done.Visible = false;

        _body = new Panel { Dock = DockStyle.Fill, BackColor = Page, Padding = new Padding(24, 18, 24, 12) };
        _body.Controls.Add(_done);
        _body.Controls.Add(_options);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Page, Margin = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(_body, 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);

        AcceptButton = _install;
        CancelButton = _cancel;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitLayout();
        _install.Focus();
    }

    /// <summary>窗口按 DPI 缩放完以后，按实际宽度定死卡片宽度和每段文字的高度。</summary>
    void FitLayout()
    {
        var width = _body.ClientSize.Width - _body.Padding.Horizontal;
        foreach (var card in _cards)
            card.MinimumSize = card.MaximumSize = new Size(width - card.Margin.Horizontal, 0);
        foreach (var label in _wrapped)
        {
            var room = RoomFor(label, width);
            label.Size = new Size(room, label.Measure(room).Height);
        }

        _status.Height = _status.Measure(200).Height;
        _options.PerformLayout();
        _done.PerformLayout();
    }

    /// <summary>文字能用的宽度：页面宽度减去外层卡片的内边距，以及同一行左边的图标。</summary>
    int RoomFor(Control label, int width)
    {
        var room = width - label.Margin.Horizontal;
        for (var c = label.Parent; c != null && c != _options && c != _done; c = c.Parent)
            room -= c.Padding.Horizontal + c.Margin.Horizontal;
        if (label.Parent is TableLayoutPanel table && table.GetColumn(label) == 1 && table.GetControlFromPosition(0, 0) is { } lead)
            room -= lead.Width + lead.Margin.Horizontal;
        return Math.Max(80, room - Sc(2));
    }

    int Sc(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        if (_finished && _launch.Checked && !_previewOnly)
            Launch();
        base.OnFormClosing(e);
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

        using (var stream = typeof(SetupForm).Assembly.GetManifestResourceStream("VibelishSetup.app.ico"))
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
            Text = IsUpdate ? "更新 Vibelish" : "安装 Vibelish",
            AutoSize = true,
            Font = Theme.Ui(15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(243, 246, 250),
            Margin = new Padding(0, 0, 0, 2),
            UseMnemonic = false,
        }, 1, 0);
        header.Controls.Add(new Label
        {
            Text = "版本 " + InstallerCore.ProductVersion + " · 写 prompt 的时候，顺手学英语",
            AutoSize = true,
            Font = Theme.Ui(8.5F),
            ForeColor = Color.FromArgb(148, 163, 184),
            Margin = new Padding(1, 0, 0, 0),
            UseMnemonic = false,
        }, 1, 1);
        return header;
    }

    Panel BuildOptions()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Page };
        var stack = Stack();

        string intro;
        if (!IsUpdate)
            intro = "装到当前用户目录，不需要管理员权限，也不需要另外安装 .NET。";
        else if (_installed == InstallerCore.ProductVersion)
            intro = "已经安装了 " + _installed + "，将重新安装同一版本。设置和 API Key 都会保留。";
        else
            intro = "已安装 " + _installed + "，将更新到 " + InstallerCore.ProductVersion + "。设置和 API Key 都会保留。";
        if (Process.GetProcessesByName("Vibelish").Length > 0)
            intro += "\nVibelish 正在运行，安装时会先把它关掉。";
        stack.Controls.Add(Wrapped(intro, Body, new Padding(2, 0, 0, 12)));

        var location = NewCard("安装位置");
        var path = new TextLabel
        {
            Text = InstallerCore.TargetDirectory,
            SingleLine = true,
            ForeColor = Body,
            Font = Theme.Ui(9F),
            Margin = new Padding(0, 0, 0, 4),
        };
        _wrapped.Add(path);
        location.Add(path);
        location.Add(Wrapped("开始菜单里会添加「Vibelish」和「卸载 Vibelish」。\n以后也可以在 Windows 设置 → 应用 里卸载。", Hint, new Padding(0, 2, 0, 0)));
        stack.Controls.Add(location);

        var choices = NewCard("选项");
        choices.Add(_startup);
        choices.Add(_desktop);
        stack.Controls.Add(choices);

        _error.Margin = new Padding(2, 4, 0, 0);
        _wrapped.Add(_error);
        stack.Controls.Add(_error);

        page.Controls.Add(stack);
        return page;
    }

    Panel BuildDone()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Page };
        var stack = Stack();

        var top = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Page,
            Margin = new Padding(0, 4, 0, 14),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var mark = new CheckMark { Size = new Size(44, 44), Margin = new Padding(0, 0, 14, 0) };
        top.Controls.Add(mark, 0, 0);
        top.SetRowSpan(mark, 2);
        _doneTitle.Margin = new Padding(0, 1, 0, 2);
        _wrapped.Add(_doneTitle);
        top.Controls.Add(_doneTitle, 1, 0);
        _doneSubtitle.Margin = new Padding(1, 0, 0, 0);
        _wrapped.Add(_doneSubtitle);
        top.Controls.Add(_doneSubtitle, 1, 1);
        stack.Controls.Add(top);

        var hotkey = ConfiguredHotkey();
        var usage = NewCard("怎么用");
        usage.Add(Step(1, "在任意输入框里按 " + hotkey + " 开始。"));
        usage.Add(Step(2, "照常打字：写中文，卡片上出现英文；直接写英文，卡片上出现中文意思。"));
        usage.Add(Step(3, "按 Enter 完成（Shift+Enter 换行），按 Esc 取消。点英文里的单词可以查词、换说法。"));
        usage.Add(Wrapped("右键托盘图标可以打开设置、切换翻译方向。\n配置大模型接口（DeepSeek、通义千问、Kimi 等）后，译文更地道，写英文时还会给出更地道的说法。", Hint, new Padding(0, 6, 0, 0)));
        stack.Controls.Add(usage);

        _launch.Margin = new Padding(2, 0, 0, 0);
        stack.Controls.Add(_launch);
        page.Controls.Add(stack);
        return page;
    }

    Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.White,
            Padding = new Padding(24, 12, 24, 14),
            Margin = new Padding(0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(CardLine);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        _progress.Dock = DockStyle.Fill;
        _progress.Height = 4;
        _progress.Margin = new Padding(0, 0, 0, 8);
        footer.Controls.Add(_progress, 0, 0);
        footer.SetColumnSpan(_progress, 3);

        _status.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _status.Margin = new Padding(0, 0, 12, 0);
        footer.Controls.Add(_status, 0, 1);

        _cancel.Text = "取消";
        _cancel.Font = Theme.Ui(9.5F);
        _cancel.Size = new Size(92, 34);
        _cancel.Margin = new Padding(0, 0, 10, 0);
        _cancel.Click += (_, _) => Close();
        footer.Controls.Add(_cancel, 1, 1);

        _install.Text = IsUpdate ? "更新" : "安装";
        _install.Font = Theme.Ui(9.5F, FontStyle.Bold);
        _install.Size = new Size(108, 34);
        _install.Margin = new Padding(0);
        _install.TabStop = true;
        _install.Click += InstallClicked;
        footer.Controls.Add(_install, 2, 1);

        var space = InstallerCore.RequiredSpaceMb;
        _status.Text = space > 0 ? "需要约 " + space + " MB 磁盘空间" : "";
        return footer;
    }

    async void InstallClicked(object? sender, EventArgs e)
    {
        if (_finished)
        {
            Close();
            return;
        }

        if (_installing)
            return;

        _installing = true;
        SetBusy(true);
        _error.Visible = false;
        _status.ForeColor = Hint;
        _status.Text = "正在准备…";
        _progress.Value = 0;
        _progress.Showing = true;

        var startup = _startup.Checked;
        var desktop = _desktop.Checked;
        var steps = 0;
        try
        {
            await Task.Run(() => InstallerCore.Install(startup, desktop, message =>
                BeginInvoke((Action)(() =>
                {
                    steps++;
                    _status.Text = message;
                    _progress.Value = Math.Min(1f, steps / 5f);
                }))));

            _installing = false;
            _finished = true;
            ShowDone();
        }
        catch (Exception ex)
        {
            _installing = false;
            _progress.Showing = false;
            _status.ForeColor = Bad;
            _status.Text = "安装没有完成";
            _error.Text = "出错了：" + ex.Message + "\n可以关掉正在运行的 Vibelish 后点「重试」。";
            _error.Visible = true;
            FitLayout();
            _install.Text = "重试";
            SetBusy(false);
        }
    }

    /// <summary>预览截图用：不安装，直接显示完成页或出错状态。</summary>
    internal void Preview(string page)
    {
        _previewOnly = true;
        if (page == "done")
        {
            _finished = true;
            ShowDone();
        }
        else if (page == "error")
        {
            _status.ForeColor = Bad;
            _status.Text = "安装没有完成";
            _error.Text = "出错了：对路径“Vibelish.exe”的访问被拒绝。\n可以关掉正在运行的 Vibelish 后点「重试」。";
            _error.Visible = true;
            _install.Text = "重试";
            FitLayout();
        }
        else if (page == "busy")
        {
            SetBusy(true);
            _status.Text = "正在复制程序文件…";
            _progress.Value = 0.4f;
            _progress.Showing = true;
        }
    }

    void ShowDone()
    {
        _doneTitle.Text = IsUpdate ? "已更新到 " + InstallerCore.ProductVersion : "安装完成";
        _doneSubtitle.Text = _startup.Checked
            ? "Vibelish 会在托盘常驻，开机自动启动。"
            : "Vibelish 会在托盘常驻；需要时从开始菜单打开。";

        _options.Visible = false;
        _done.Visible = true;
        FitLayout();

        _progress.Showing = false;
        _status.ForeColor = Good;
        _status.Text = "全部完成";
        _cancel.Visible = false;
        _install.Text = "完成";
        _install.Enabled = true;
        _install.Focus();
    }

    void SetBusy(bool busy)
    {
        _install.Enabled = !busy;
        _cancel.Enabled = !busy;
        _startup.Enabled = !busy;
        _desktop.Enabled = !busy;
        _launch.Enabled = !busy;
        UseWaitCursor = busy;
    }

    static void Launch()
    {
        try
        {
            if (File.Exists(InstallerCore.ExePath))
                Process.Start(new ProcessStartInfo(InstallerCore.ExePath) { UseShellExecute = true, WorkingDirectory = InstallerCore.TargetDirectory });
        }
        catch
        {
            // Ignore launch failures.
        }
    }

    /// <summary>用户改过热键的话，完成页显示改过的。</summary>
    static string ConfiguredHotkey()
    {
        try
        {
            // 从旧版「输入翻译」更新时，新版第一次启动才会把设置搬过来。
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var file = Path.Combine(appData, "Vibelish", "config.json");
            if (!File.Exists(file))
                file = Path.Combine(appData, "InputTranslate", "config.json");
            if (File.Exists(file))
            {
                var match = Regex.Match(File.ReadAllText(file), "\"Hotkey\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success && match.Groups[1].Value.Trim().Length > 0)
                    return match.Groups[1].Value.Trim();
            }
        }
        catch
        {
            // Ignore.
        }

        return "Ctrl+Alt+T";
    }

    static FlowLayoutPanel Stack() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = Page,
        Margin = new Padding(0),
        Padding = new Padding(0),
    };

    Card NewCard(string title)
    {
        var card = new Card(title);
        _cards.Add(card);
        _wrapped.Add(card.Heading);
        return card;
    }

    TextLabel Wrapped(string text, Color color, Padding margin)
    {
        var label = new TextLabel { Text = text, ForeColor = color, Font = Theme.Ui(9F), Margin = margin };
        _wrapped.Add(label);
        return label;
    }

    Control Step(int number, string text)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 4),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new StepBadge(number) { Size = new Size(20, 20), Margin = new Padding(0, 0, 10, 0) }, 0, 0);
        row.Controls.Add(Wrapped(text, Body, new Padding(0, 1, 0, 0)), 1, 0);
        return row;
    }

    static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        ForeColor = Ink,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 3, 0, 3),
        UseMnemonic = false,
    };

    /// <summary>白底圆角的一组内容。</summary>
    sealed class Card : FlowLayoutPanel
    {
        public Card(string title)
        {
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = CardBack;
            Padding = new Padding(18, 14, 18, 12);
            Margin = new Padding(0, 0, 0, 12);
            DoubleBuffered = true;
            // TextLabel 不带 Label 的左内边距，标题和下面的文字、复选框左边对齐。
            Heading = new TextLabel
            {
                Text = title,
                SingleLine = true,
                Font = Theme.Ui(10.5F, FontStyle.Bold),
                ForeColor = Ink,
                Margin = new Padding(0, 0, 0, 8),
            };
            Controls.Add(Heading);
        }

        public TextLabel Heading { get; }

        public void Add(Control control) => Controls.Add(control);

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

    /// <summary>细进度条，比系统的 ProgressBar 好看，也不会在 Windows 10 上闪。</summary>
    sealed class ProgressLine : Control
    {
        float _value;
        bool _showing;

        public ProgressLine()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public float Value
        {
            get => _value;
            set
            {
                _value = Math.Max(0f, Math.Min(1f, value));
                Invalidate();
            }
        }

        /// <summary>不显示时只留白，占着位置，页脚不会跳。</summary>
        public bool Showing
        {
            get => _showing;
            set
            {
                _showing = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (!_showing)
                return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            var radius = Math.Max(1, Height / 2);
            using (var path = Theme.RoundedRect(track, radius))
            using (var brush = new SolidBrush(Color.FromArgb(226, 232, 240)))
                g.FillPath(brush, path);
            var width = (int)Math.Round((Width - 1) * _value);
            if (width < Height)
                return;
            using (var path = Theme.RoundedRect(new Rectangle(0, 0, width, Height - 1), radius))
            using (var brush = new SolidBrush(Accent))
                g.FillPath(brush, path);
        }
    }

    /// <summary>完成页的绿色对勾。</summary>
    sealed class CheckMark : Control
    {
        public CheckMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Page;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var size = Math.Min(Width, Height) - 1;
            using (var brush = new SolidBrush(Good))
                g.FillEllipse(brush, 0, 0, size, size);
            var s = size / 44f;
            using var pen = new Pen(Color.White, 3.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(13 * s, 23 * s),
                new PointF(19.5f * s, 29.5f * s),
                new PointF(31 * s, 16 * s),
            });
        }
    }

    /// <summary>用法步骤前的圆形序号。</summary>
    sealed class StepBadge : Control
    {
        readonly int _number;

        public StepBadge(int number)
        {
            _number = number;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = CardBack;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var size = Math.Min(Width, Height) - 1;
            using (var brush = new SolidBrush(Color.FromArgb(225, 238, 253)))
                g.FillEllipse(brush, 0, 0, size, size);
            TextRenderer.DrawText(g, _number.ToString(), Theme.Ui(8.5F, FontStyle.Bold), new Rectangle(0, 0, size + 1, size + 1), Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}
