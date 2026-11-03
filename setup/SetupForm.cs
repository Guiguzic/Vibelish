using System.Diagnostics;
using InputTranslate;

namespace InputTranslateSetup;

sealed class SetupForm : Form
{
    readonly CheckBox _startup = new() { Text = "开机自动启动（推荐）", Checked = true, AutoSize = true };
    readonly CheckBox _desktop = new() { Text = "创建桌面快捷方式", Checked = false, AutoSize = true };
    readonly CheckBox _launch = new() { Text = "安装完成后启动输入翻译", Checked = true, AutoSize = true };
    readonly Label _status = new();
    readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28, Visible = false };
    readonly RoundButton _install = new();
    readonly RoundButton _cancel = new();
    readonly Panel _header;
    readonly Panel _footer;
    readonly Panel _body;
    bool _installing;

    public SetupForm()
    {
        Text = "输入翻译 安装程序";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        BackColor = Color.FromArgb(248, 250, 252);
        ClientSize = new Size(620, 450);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        using (var iconStream = typeof(SetupForm).Assembly.GetManifestResourceStream("InputTranslateSetup.app.ico"))
        {
            if (iconStream != null)
                Icon = new Icon(iconStream);
        }

        _header = BuildHeader();
        _body = BuildBody();
        _footer = BuildFooter();

        Controls.Add(_body);
        Controls.Add(_footer);
        Controls.Add(_header);
        _header.Dock = DockStyle.Top;
        _footer.Dock = DockStyle.Bottom;
        _body.Dock = DockStyle.Fill;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var scale = DeviceDpi / 96.0;
        _header.Height = (int)Math.Round(92 * scale);
        _footer.Height = (int)Math.Round(110 * scale);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    Panel BuildHeader()
    {
        var header = new Panel { BackColor = Color.FromArgb(15, 23, 42), Height = 92 };
        header.Controls.Add(new Label
        {
            Text = "输入翻译",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
            ForeColor = Color.FromArgb(248, 250, 252),
            Location = new Point(28, 16),
        });
        header.Controls.Add(new Label
        {
            Text = "中文输入 → 考研级英文 · 安装程序 " + InstallerCore.ProductVersion,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(30, 64),
        });

        using var stream = typeof(SetupForm).Assembly.GetManifestResourceStream("InputTranslateSetup.app.ico");
        if (stream != null)
        {
            using var icon = new Icon(stream, 48, 48);
            header.Controls.Add(new PictureBox
            {
                Image = icon.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(48, 48),
                BackColor = Color.Transparent,
                Location = new Point(620 - 28 - 48, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            });
        }

        return header;
    }

    Panel BuildBody()
    {
        var body = new Panel { Padding = new Padding(30, 20, 30, 16), BackColor = Color.FromArgb(248, 250, 252) };

        body.Controls.Add(new Label
        {
            Text = "将安装到当前用户目录，无需管理员权限。",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(30, 18),
        });

        body.Controls.Add(new Label
        {
            Text = "安装位置",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(51, 65, 85),
            Location = new Point(30, 54),
        });
        body.Controls.Add(new Label
        {
            Text = InstallerCore.TargetDirectory,
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(30, 78),
        });

        _startup.Location = new Point(30, 122);
        _desktop.Location = new Point(30, 152);
        _launch.Location = new Point(30, 182);
        body.Controls.Add(_startup);
        body.Controls.Add(_desktop);
        body.Controls.Add(_launch);

        body.Controls.Add(new Label
        {
            Text = "安装后在任意输入框里输入中文，按热键开始，回车结束。",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(30, 226),
        });

        return body;
    }

    Panel BuildFooter()
    {
        var footer = new Panel { BackColor = Color.White, Height = 110 };
        _progress.Left = 30;
        _progress.Top = 12;
        _progress.Width = 560;
        _progress.Height = 7;
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        footer.Controls.Add(_progress);

        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(100, 116, 139);
        _status.Location = new Point(30, 26);
        footer.Controls.Add(_status);

        _install.Text = "安装";
        _install.Width = 116;
        _install.Height = 38;
        _install.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _install.CornerRadius = 9;
        _install.NormalBack = Color.FromArgb(14, 165, 233);
        _install.HoverBack = Color.FromArgb(2, 132, 199);
        _install.PressedBack = Color.FromArgb(3, 105, 161);
        _install.BorderColor = Color.FromArgb(14, 165, 233);
        _install.HoverBorderColor = Color.FromArgb(2, 132, 199);
        _install.TextColor = Color.White;
        _install.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _install.Click += InstallClicked;

        _cancel.Text = "取消";
        _cancel.Width = 96;
        _cancel.Height = 38;
        _cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _cancel.CornerRadius = 9;
        _cancel.NormalBack = Color.White;
        _cancel.HoverBack = Color.FromArgb(241, 245, 249);
        _cancel.PressedBack = Color.FromArgb(226, 232, 240);
        _cancel.BorderColor = Color.FromArgb(203, 213, 225);
        _cancel.HoverBorderColor = Color.FromArgb(148, 163, 184);
        _cancel.TextColor = Color.FromArgb(51, 65, 85);
        _cancel.Font = new Font("Microsoft YaHei UI", 9.5F);
        _cancel.Click += (_, _) => Close();

        footer.Controls.Add(_cancel);
        footer.Controls.Add(_install);
        footer.Resize += (_, _) =>
        {
            var top = footer.Height - _install.Height - 14;
            _install.Location = new Point(footer.Width - 30 - _install.Width, top);
            _cancel.Location = new Point(_install.Left - 12 - _cancel.Width, top);
            _progress.Width = Math.Max(20, footer.Width - 60);
            _progress.Top = 12;
            _status.Top = 26;
        };

        AcceptButton = _install;
        return footer;
    }

    async void InstallClicked(object? sender, EventArgs e)
    {
        if (_installing)
            return;

        _installing = true;
        SetBusy(true);
        _status.Text = "正在安装…";
        _progress.Visible = true;

        try
        {
            await Task.Run(() =>
            {
                InstallerCore.Install(
                    _startup.Checked,
                    _desktop.Checked,
                    message => BeginInvoke(() => _status.Text = message));
            });

            _progress.Visible = false;
            _status.Text = "安装完成";
            _installing = false;

            if (_launch.Checked)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(InstallerCore.ExePath) { UseShellExecute = true });
                }
                catch
                {
                    // Ignore launch failures.
                }
            }

            MessageBox.Show(this,
                "「输入翻译」已安装完成。\n\n托盘会常驻运行，按热键开始翻译（卡片会提示实际热键）。\n需要卸载时可在「设置 → 应用」或开始菜单里操作。",
                "输入翻译",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _installing = false;
            _progress.Visible = false;
            _status.Text = "安装失败";
            MessageBox.Show(this, "安装失败：" + ex.Message, "输入翻译", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        _install.Enabled = !busy;
        _cancel.Enabled = !busy;
        _startup.Enabled = !busy;
        _desktop.Enabled = !busy;
        _launch.Enabled = !busy;
        _cancel.Text = busy ? "安装中…" : "取消";
    }
}
