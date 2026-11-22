namespace Vibelish;

/// <summary>
/// 学习记录：概览（今天写了多少、最常犯的错、大模型诊断）、错题本、中译英、复习。
/// 外观和设置窗口一样：左边侧栏，右边一组一组的卡片。
/// </summary>
sealed class NotebookForm : Form
{
    const string OverviewGlyph = "";
    const string MistakeGlyph = "";
    const string TranslateGlyph = "";
    const string ReviewGlyph = "";

    readonly Notebook _notebook;
    readonly Func<AppConfig> _config;
    readonly Func<string, CancellationToken, Task<TranslateResult>> _diagnose;
    readonly Panel _sidebar = new();
    readonly Panel _content = new();
    readonly NavList _nav = new();
    readonly TextLabel _appName = new() { SingleLine = true, Text = "学习记录" };
    readonly TextLabel _appNote = new() { SingleLine = true };
    readonly PictureBox _appIcon = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
    readonly SettingsPage _overview = new("概览");
    readonly SettingsPage _mistakes = new("错题本");
    readonly SettingsPage _translations = new("中译英");
    readonly SettingsPage _review = new("复习");
    readonly SettingsPage[] _pages;
    readonly ReviewCard _card;
    readonly RoundButton _diagnoseButton = new() { Text = "开始分析", CornerRadius = 99, Variant = ButtonVariant.Primary };
    SettingsRow? _diagnoseRow;
    TextLabel? _reportLabel;
    SettingsRow? _reviewHeader;
    CancellationTokenSource? _diagnosing;
    bool _dirty;

    public NotebookForm(Notebook notebook, Func<AppConfig> config, Func<string, CancellationToken, Task<TranslateResult>> diagnose)
    {
        _notebook = notebook;
        _config = config;
        _diagnose = diagnose;
        _card = new ReviewCard(notebook, config);
        _pages = [_overview, _mistakes, _translations, _review];
        Text = "Vibelish 学习记录";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Ui(9F);
        KeyPreview = true;
        DoubleBuffered = true;
        using (var stream = typeof(NotebookForm).Assembly.GetManifestResourceStream("Vibelish.app.ico"))
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
        ClientSize = new Size((int)(860 * scale), (int)(640 * scale));
        MinimumSize = new Size((int)(640 * scale), (int)(460 * scale));

        _nav.Add(OverviewGlyph, "概览");
        _nav.Add(MistakeGlyph, "错题本");
        _nav.Add(TranslateGlyph, "中译英");
        _nav.Add(ReviewGlyph, "复习");
        foreach (var page in _pages)
        {
            page.Visible = false;
            _content.Controls.Add(page);
        }

        _sidebar.Controls.AddRange(new Control[] { _appIcon, _appName, _appNote, _nav });
        Controls.Add(_content);
        Controls.Add(_sidebar);
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        _diagnoseButton.Click += async (_, _) => await DiagnoseAsync();
        _card.Graded += (_, _) => UpdateReviewHeader();
        _notebook.Changed += OnNotebookChanged;
        ShowPage(0);
        ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeTheme.TitleBar(this, Theme.Current);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _notebook.Changed -= OnNotebookChanged;
        _diagnosing?.Cancel();
        base.OnFormClosed(e);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var scale = DeviceDpi / 96.0;
        int Sc(int value) => (int)Math.Round(value * scale);
        var sidebarWidth = Sc(200);
        _sidebar.SetBounds(0, 0, sidebarWidth, ClientSize.Height);
        _content.SetBounds(sidebarWidth, 0, Math.Max(10, ClientSize.Width - sidebarWidth), ClientSize.Height);
        _appIcon.SetBounds(Sc(20), Sc(20), Sc(34), Sc(34));
        _appName.SetBounds(_appIcon.Right + Sc(10), Sc(19), sidebarWidth - _appIcon.Right - Sc(16), _appName.Measure(200).Height);
        _appNote.SetBounds(_appName.Left, _appName.Bottom + Sc(1), _appName.Width, _appNote.Measure(200).Height);
        _nav.SetBounds(0, Sc(74), sidebarWidth, _nav.PreferredHeight + Sc(4));
        foreach (var page in _pages)
            page.SetBounds(0, 0, _content.Width, _content.Height);
    }

    public void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Window;
        _sidebar.BackColor = p.Sidebar;
        _nav.BackColor = p.Sidebar;
        _content.BackColor = p.Window;
        _appName.Font = Theme.Ui(11F, FontStyle.Bold);
        _appName.ForeColor = p.Text;
        _appNote.Font = Theme.Ui(8.25F);
        _appNote.ForeColor = p.Muted;
        _diagnoseButton.Font = Theme.Ui(9F);
        _diagnoseButton.Size = new Size(_diagnoseButton.PreferredWidth(Theme.Scale(this, 16)), Theme.Scale(this, 30));
        _card.ApplyTheme();
        foreach (var page in _pages)
            page.ApplyTheme();
        NativeTheme.TitleBar(this, p);
        Invalidate(true);
    }

    /// <summary>切到某一页（预览截图、托盘「开始复习」也用）。</summary>
    internal void ShowPage(int index)
    {
        index = Math.Max(0, Math.Min(_pages.Length - 1, index));
        if (_nav.SelectedIndex != index)
            _nav.SelectedIndex = index;
        Build(index);
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visible = i == index;
        _pages[index].Arrange();
        UpdateNote();
    }

    void OnNotebookChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;
        UpdateNote();
        // 复习页自己会更新；别的页正在看就重建，没在看的等切过去再建。
        var current = _nav.SelectedIndex;
        if (current == 3)
        {
            _dirty = true;
            return;
        }

        Build(current);
        _pages[current].Arrange();
    }

    void UpdateNote()
    {
        var stats = _notebook.Stats();
        _appNote.Text = stats.Due > 0 ? "待复习 " + stats.Due + " 条" : stats.Streak > 0 ? "连续学习 " + stats.Streak + " 天" : "写得越多，记录越准";
    }

    void Build(int index)
    {
        var page = _pages[index];
        // 重建会把旧的分组连同里面的控件一起释放；一直复用的按钮和复习卡先拿出来。
        _diagnoseButton.Parent?.Controls.Remove(_diagnoseButton);
        if (index == 3 && (_reviewHeader == null || _dirty))
            _card.Parent?.Controls.Remove(_card);
        switch (index)
        {
            case 0:
                BuildOverview(page);
                break;
            case 1:
                BuildMistakes(page);
                break;
            case 2:
                BuildTranslations(page);
                break;
            default:
                if (_reviewHeader == null || _dirty)
                {
                    BuildReview(page);
                }
                else if (_card.Progress.Index >= _card.Progress.Count)
                {
                    _card.Start();
                    UpdateReviewHeader();
                }

                break;
        }

        _dirty = false;
        page.ApplyTheme();
    }

    static string Day(DateTime time)
    {
        var today = DateTime.Today;
        if (time.Date == today)
            return "今天";
        if (time.Date == today.AddDays(-1))
            return "昨天";
        return time.Year == today.Year ? time.ToString("M月d日") : time.ToString("yyyy年M月d日");
    }

    void BuildOverview(SettingsPage page)
    {
        _diagnoseRow = null;
        _reportLabel = null;
        page.ClearItems();
        var config = _config();
        var stats = _notebook.Stats();
        var today = page.AddGroup("今天");
        today.AddRow("", "", new StatTiles(
        [
            (stats.Written.ToString(), "写" + config.Target.Name + "（句）"),
            (stats.Fixed.ToString(), "被纠正（处）"),
            (stats.Translated.ToString(), "中译英（句）"),
            (stats.Streak.ToString(), "连续学习（天）"),
        ])).Stacked = true;
        var start = LinkButton(stats.Due > 0 ? "开始复习" : "去看看", () => ShowPage(3));
        today.AddRow("待复习 " + stats.Due + " 条", "错题和中译英按间隔重复安排：记住了隔 1、2、4、7、15、30 天再出现", start);
        today.AddRow("累计",
            "写了 " + stats.TotalWritten + " 句" + config.Target.Name + "，被纠正 " + stats.TotalFixes + " 处"
            + (stats.CleanRate >= 0 ? "；最近 30 天一处错都没有的句子占 " + stats.CleanRate + "%" : ""),
            null);

        var top = _notebook.TopMistakes();
        var mistakes = page.AddGroup("最常犯的错误（最近 30 天）");
        if (top.Count == 0)
        {
            mistakes.AddRow("还没有数据", "用" + config.Target.Name + "写几句、按 Enter 完成，这里会按类型统计你的错误（需要大模型接口）", null);
        }
        else
        {
            mistakes.AddRow("", "", new MistakeBars(top.Take(6).Select(item => (
                Coaching.Label(item.Type, config.Native, config.Target),
                item.Type,
                item.Count,
                "例：" + item.Example.Wrong + " → " + item.Example.Right + (item.Example.Why.Length > 0 ? "（" + item.Example.Why + "）" : ""))).ToArray())).Stacked = true;
        }

        var ai = page.AddGroup("大模型诊断");
        _reportLabel = new TextLabel { Text = _notebook.Report.Length > 0 ? _notebook.Report : "写满 5 句" + config.Target.Name + "以后，可以让大模型看看你最近的错误：最常犯哪几类、背后是什么母语思维、该怎么练，再出 3 句针对性的翻译练习。" };
        ai.AddRow("", "", _reportLabel).Stacked = true;
        _diagnoseRow = ai.AddRow(_notebook.Report.Length > 0 ? "重新分析" : "分析我的问题", DiagnoseSubtitle(config), _diagnoseButton);
        _diagnoseButton.Enabled = _diagnosing == null;
        page.AddNote("只记按 Enter 完成、并且用大模型翻译的句子。记录只存在这台电脑的 " + Notebook.DefaultPath + "。");
    }

    string DiagnoseSubtitle(AppConfig config)
    {
        if (!config.HasChatApi)
            return "需要大模型接口：设置 → 大模型（推荐 DeepSeek）";
        if (_notebook.WrittenCount < 5)
            return "现在只有 " + _notebook.WrittenCount + " 句，写满 5 句再分析更准";
        return _notebook.ReportTime is { } time ? "上次分析：" + Day(time) + time.ToString(" HH:mm") + "。会把最近 40 句和它们的错误发给大模型"
            : "会把最近 40 句和它们的错误发给大模型";
    }

    async Task DiagnoseAsync()
    {
        var config = _config();
        if (!config.HasChatApi || _notebook.WrittenCount < 5)
        {
            if (_diagnoseRow != null)
            {
                _diagnoseRow.Subtitle = DiagnoseSubtitle(config);
                _diagnoseRow.SubtitleColor = Theme.Current.Error;
            }

            return;
        }

        _diagnosing?.Cancel();
        var cts = new CancellationTokenSource();
        _diagnosing = cts;
        _diagnoseButton.Enabled = false;
        if (_diagnoseRow != null)
            _diagnoseRow.Subtitle = "正在分析，大约要 10–30 秒…";
        try
        {
            var result = await _diagnose(_notebook.DiagnosisMaterial(), cts.Token);
            if (cts.IsCancellationRequested || IsDisposed)
                return;
            if (!result.Ok)
            {
                if (_diagnoseRow != null)
                {
                    _diagnoseRow.Subtitle = "没分析成功：" + result.Detail;
                    _diagnoseRow.SubtitleColor = Theme.Current.Error;
                }

                return;
            }

            _diagnosing = null;
            _notebook.SetReport(result.Text); // 触发 Changed，概览页会重建。
            TraceLog.Note("diagnose ok chars=" + result.Text.Length);
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
        finally
        {
            if (_diagnosing == cts)
                _diagnosing = null;
            if (!IsDisposed)
                _diagnoseButton.Enabled = true;
        }
    }

    void BuildMistakes(SettingsPage page)
    {
        page.ClearItems();
        var entries = _notebook.Entries.Where(entry => entry.Kind == "write" && entry.Fixes.Count > 0)
            .OrderByDescending(entry => entry.Time).Take(120).ToList();
        if (entries.Count == 0)
        {
            var empty = page.AddGroup("");
            empty.AddRow("还没有错题", "直接用" + _config().Target.Name + "写、按 Enter 完成。写错的地方会记在这里：写错的 → 改正的，为什么错，背后的母语思维。", null);
            return;
        }

        foreach (var day in entries.GroupBy(entry => entry.Time.Date))
        {
            var group = page.AddGroup(Day(day.Key) + " · " + day.Count() + " 句");
            foreach (var entry in day)
            {
                var native = Languages.Find(entry.Native);
                var target = Languages.Find(entry.Target);
                var lines = entry.Fixes.Select(fix => "✗ " + fix.Wrong + "  →  " + fix.Right + "    " + Coaching.Label(fix.Type, native, target)
                    + (fix.Why.Length > 0 ? "：" + fix.Why : "")).ToList();
                var corrected = Notebook.ApplyFixes(entry.Source, entry.Fixes);
                if (corrected != entry.Source)
                    lines.Add("改正：" + corrected);
                if (entry.Better.Length > 0 && !Lang.SameSentence(entry.Better, corrected))
                    lines.Add("更地道：" + entry.Better);
                if (entry.Habit.Length > 0)
                    lines.Add("思维：" + entry.Habit);
                var item = entry;
                group.AddRow(entry.Source, string.Join("\n", lines), DeleteButton(() => _notebook.Delete(item)));
            }
        }
    }

    void BuildTranslations(SettingsPage page)
    {
        page.ClearItems();
        var entries = _notebook.Entries.Where(entry => entry.Kind == "translate")
            .OrderByDescending(entry => entry.Time).Take(120).ToList();
        if (entries.Count == 0)
        {
            var empty = page.AddGroup("");
            empty.AddRow("还没有记录", "写中文、按 Enter 完成，原文和大模型的译文会记在这里，复习时先看中文、自己想英文怎么说。", null);
            return;
        }

        foreach (var day in entries.GroupBy(entry => entry.Time.Date))
        {
            var group = page.AddGroup(Day(day.Key) + " · " + day.Count() + " 句");
            foreach (var entry in day)
            {
                var item = entry;
                group.AddRow(entry.Source, entry.Result + (entry.Tip.Length > 0 ? "\n点拨：" + entry.Tip : ""), DeleteButton(() => _notebook.Delete(item)));
            }
        }
    }

    void BuildReview(SettingsPage page)
    {
        page.ClearItems();
        var group = page.AddGroup("");
        _reviewHeader = group.AddRow("", "", null);
        group.AddRow("", "", _card).Stacked = true;
        _card.Start();
        UpdateReviewHeader();
        page.AddNote("快捷键：空格 显示答案 · ← 没想起来 · → 记住了");
    }

    void UpdateReviewHeader()
    {
        if (_reviewHeader == null || _reviewHeader.IsDisposed)
            return;
        var (index, count) = _card.Progress;
        var due = _notebook.Stats().Due;
        _reviewHeader.Visible = count > 0 || due > 0;
        _reviewHeader.Subtitle = index < count ? "这一轮第 " + (index + 1) + " / " + count + " 条，一共 " + due + " 条到点该复习" : "还有 " + due + " 条到点该复习";
        UpdateNote();
    }

    RoundButton LinkButton(string text, Action click)
    {
        var button = new RoundButton { Text = text, CornerRadius = 99, Font = Theme.Ui(9F) };
        button.Size = new Size(button.PreferredWidth(Theme.Scale(this, 16)), Theme.Scale(this, 28));
        button.Click += (_, _) => click();
        return button;
    }

    RoundButton DeleteButton(Action click)
    {
        var button = new RoundButton { Glyph = Glyphs.Close, Variant = ButtonVariant.Danger, CornerRadius = 99, Font = Theme.Ui(9F) };
        if (Theme.Icons(9F) == null)
            button.Text = "删除";
        button.Size = new Size(Theme.Scale(this, 28), Theme.Scale(this, 28));
        new ToolTip().SetToolTip(button, "从学习记录里删掉");
        button.Click += (_, _) => click();
        return button;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        if (_nav.SelectedIndex == 3 && _card.HandleKey(keyData))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>预览截图用：复习页直接翻开答案。</summary>
    internal void PreviewReveal() => _card.HandleKey(Keys.Space);

    /// <summary>每类错误一条横条，长短按次数，下面一行是最近的例子。</summary>
    sealed class MistakeBars : ThemedControl, IFlowControl
    {
        readonly (string Label, string Type, int Count, string Example)[] _items;
        readonly List<int> _tops = new();
        int _labelWidth;

        public MistakeBars((string Label, string Type, int Count, string Example)[] items)
        {
            _items = items;
            TabStop = false;
        }

        static Font LabelFont => Theme.Ui(9.5F);

        static Font NoteFont => Theme.Ui(8.25F);

        public int Arrange(int width)
        {
            _tops.Clear();
            _labelWidth = _items.Length == 0 ? 0
                : _items.Max(item => TextRenderer.MeasureText(item.Label, LabelFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width) + Sc(14);
            var y = 0;
            foreach (var _ in _items)
            {
                _tops.Add(y);
                y += LabelFont.Height + Sc(4) + NoteFont.Height + Sc(14);
            }

            return Math.Max(0, y - Sc(14));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(P.Group);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var most = _items.Length == 0 ? 1 : Math.Max(1, _items.Max(item => item.Count));
            var countWidth = Sc(52);
            var barRoom = Math.Max(Sc(20), Width - _labelWidth - countWidth);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            for (var i = 0; i < _items.Length && i < _tops.Count; i++)
            {
                var item = _items[i];
                var top = _tops[i];
                var line = LabelFont.Height;
                TextRenderer.DrawText(g, item.Label, LabelFont, new Rectangle(0, top, _labelWidth, line), P.Text, flags);
                var color = item.Type switch
                {
                    "grammar" => P.Error,
                    "literal" => P.Warning,
                    "spelling" or "punctuation" => P.Muted,
                    _ => P.Accent,
                };
                var barHeight = Sc(10);
                var barWidth = Math.Max(barHeight, (int)Math.Round(barRoom * (double)item.Count / most));
                var bar = new Rectangle(_labelWidth, top + (line - barHeight) / 2, barWidth, barHeight);
                using (var path = Theme.RoundedRect(bar, barHeight / 2))
                using (var fill = new SolidBrush(color))
                    g.FillPath(fill, path);
                TextRenderer.DrawText(g, item.Count + " 次", LabelFont, new Rectangle(bar.Right + Sc(8), top, countWidth, line), P.Muted, flags);
                TextRenderer.DrawText(g, item.Example, NoteFont, new Rectangle(0, top + line + Sc(4), Width, NoteFont.Height), P.Muted, flags);
            }
        }
    }

    /// <summary>几个大数字并排：今天写了多少、被纠正多少……</summary>
    sealed class StatTiles : ThemedControl, IFlowControl
    {
        readonly (string Value, string Label)[] _tiles;
        readonly List<Rectangle> _boxes = new();

        public StatTiles((string Value, string Label)[] tiles)
        {
            _tiles = tiles;
            TabStop = false;
        }

        public int Arrange(int width)
        {
            _boxes.Clear();
            var gap = Sc(10);
            var columns = width >= Sc(460) ? _tiles.Length : 2;
            var tileWidth = (width - gap * (columns - 1)) / columns;
            var tileHeight = Sc(70);
            for (var i = 0; i < _tiles.Length; i++)
                _boxes.Add(new Rectangle(i % columns * (tileWidth + gap), i / columns * (tileHeight + gap), tileWidth, tileHeight));
            return (_tiles.Length + columns - 1) / columns * (tileHeight + gap) - gap;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(P.Group);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var number = Theme.Display(17F);
            var label = Theme.Ui(8.25F);
            for (var i = 0; i < _tiles.Length && i < _boxes.Count; i++)
            {
                var box = _boxes[i];
                using (var path = Theme.RoundedRect(box, Sc(8)))
                using (var fill = new SolidBrush(P.Fill))
                    g.FillPath(fill, path);
                var inner = Rectangle.Inflate(box, -Sc(12), -Sc(8));
                TextRenderer.DrawText(g, _tiles[i].Value, number, new Rectangle(inner.X, inner.Y, inner.Width, inner.Height - label.Height - Sc(2)),
                    i == 1 && _tiles[i].Value != "0" ? P.Error : P.Accent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, _tiles[i].Label, label, new Rectangle(inner.X, inner.Bottom - label.Height, inner.Width, label.Height), P.Muted,
                    TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }
}

/// <summary>
/// 复习卡：先只给题目（错题是自己写错的那句，中译英是中文），想好了再看答案，然后说记没记住。
/// </summary>
sealed class ReviewCard : ThemedControl, IFlowControl
{
    readonly Notebook _notebook;
    readonly Func<AppConfig> _config;
    readonly TextLabel _ask = new();
    readonly TextLabel _prompt = new();
    readonly TextLabel _answer = new();
    readonly RoundButton _reveal = new() { Text = "显示答案", CornerRadius = 99, Variant = ButtonVariant.Primary };
    readonly RoundButton _forgot = new() { Text = "没想起来", CornerRadius = 99 };
    readonly RoundButton _remembered = new() { Text = "记住了", CornerRadius = 99, Variant = ButtonVariant.Primary };
    readonly RoundButton _again = new() { Text = "再来一轮", CornerRadius = 99 };
    List<NoteEntry> _queue = new();
    int _index;
    bool _revealed;
    int _done;

    public ReviewCard(Notebook notebook, Func<AppConfig> config)
    {
        _notebook = notebook;
        _config = config;
        TabStop = false;
        Controls.AddRange(new Control[] { _ask, _prompt, _answer, _reveal, _forgot, _remembered, _again });
        _reveal.Click += (_, _) => Reveal();
        _forgot.Click += (_, _) => Grade(false);
        _remembered.Click += (_, _) => Grade(true);
        _again.Click += (_, _) => Start();
        ApplyTheme();
    }

    public event EventHandler? Graded;

    public (int Index, int Count) Progress => (_index, _queue.Count);

    NoteEntry? Current => _index < _queue.Count ? _queue[_index] : null;

    public void ApplyTheme()
    {
        BackColor = P.Group;
        _ask.Font = Theme.Ui(8.75F);
        _ask.ForeColor = P.Muted;
        _answer.Font = Theme.Ui(9.5F);
        _answer.ForeColor = P.Body;
        foreach (var button in new[] { _reveal, _forgot, _remembered, _again })
            button.Font = Theme.Ui(9.5F);
        ShowCurrent();
    }

    /// <summary>重新取一遍现在该复习的（一轮最多 20 条）。</summary>
    public void Start()
    {
        _queue = _notebook.DueEntries().Take(20).ToList();
        _index = 0;
        _done = 0;
        ShowCurrent();
        Relayout();
    }

    public bool HandleKey(Keys key)
    {
        if (Current == null)
            return false;
        switch (key)
        {
            case Keys.Space or Keys.Enter when !_revealed:
                Reveal();
                return true;
            case Keys.Left or Keys.D1 or Keys.NumPad1 when _revealed:
                Grade(false);
                return true;
            case Keys.Right or Keys.D2 or Keys.NumPad2 when _revealed:
                Grade(true);
                return true;
            default:
                return false;
        }
    }

    void Reveal()
    {
        if (Current == null)
            return;
        _revealed = true;
        ShowCurrent();
        Relayout();
    }

    void Grade(bool remembered)
    {
        if (Current is not { } entry)
            return;
        _notebook.Grade(entry, remembered);
        _index++;
        _done++;
        _revealed = false;
        ShowCurrent();
        Relayout();
        Graded?.Invoke(this, EventArgs.Empty);
    }

    void ShowCurrent()
    {
        var entry = Current;
        _reveal.Visible = entry != null && !_revealed;
        _forgot.Visible = _remembered.Visible = entry != null && _revealed;
        _again.Visible = entry == null && _notebook.DueEntries().Count > 0;
        _answer.Visible = entry != null && _revealed;
        if (entry == null)
        {
            var config = _config();
            _ask.Text = _done > 0 ? "这一轮复习完了，做了 " + _done + " 条。" : "";
            _prompt.Text = _done > 0 && _again.Visible ? "还有到点的，可以再来一轮。"
                : _notebook.Entries.Any(item => item.Reviewable) ? "现在没有要复习的，明天再来。"
                : "还没有可以复习的。用" + config.Target.Name + "写（写错的会进错题本），或者写中文让它翻（会进中译英），按 Enter 完成就会记下来。";
            _prompt.Font = Theme.Ui(10.5F);
            _prompt.ForeColor = P.Body;
            return;
        }

        var native = Languages.Find(entry.Native);
        var target = Languages.Find(entry.Target);
        if (entry.Kind == "write")
        {
            _ask.Text = Day(entry.Time) + "写的这句有 " + entry.Fixes.Count + " 处错误，先在心里改一改：";
            _prompt.Font = Theme.CardText(target.Font, 14F);
            var lines = new List<string>();
            var corrected = Notebook.ApplyFixes(entry.Source, entry.Fixes);
            lines.Add("改正：" + corrected);
            foreach (var fix in entry.Fixes)
                lines.Add("· " + fix.Wrong + " → " + fix.Right + "    " + Coaching.Label(fix.Type, native, target) + (fix.Why.Length > 0 ? "：" + fix.Why : ""));
            if (entry.Better.Length > 0 && !Lang.SameSentence(entry.Better, corrected))
                lines.Add("更地道：" + entry.Better);
            if (entry.Habit.Length > 0)
                lines.Add("思维：" + entry.Habit);
            _answer.Text = string.Join("\n", lines);
        }
        else
        {
            _ask.Text = "用" + target.Name + "怎么说？想好了再看答案：";
            _prompt.Font = Theme.CardText(native.Font, 13F);
            _answer.Text = entry.Result + (entry.Tip.Length > 0 ? "\n\n点拨：" + entry.Tip : "");
            _answer.Font = Theme.CardText(target.Font, 11F);
        }

        if (entry.Kind == "write")
            _answer.Font = Theme.Ui(9.5F);
        _prompt.Text = entry.Source;
        _prompt.ForeColor = P.Text;
    }

    static string Day(DateTime time) =>
        time.Date == DateTime.Today ? "今天" : time.Date == DateTime.Today.AddDays(-1) ? "昨天" : time.ToString("M月d日");

    void Relayout()
    {
        for (var c = Parent; c != null; c = c.Parent)
        {
            if (c is SettingsPage page)
            {
                page.Arrange();
                return;
            }
        }
    }

    public int Arrange(int width)
    {
        var y = 0;
        if (_ask.Text.Length > 0)
        {
            var h = _ask.Measure(width).Height;
            _ask.SetBounds(0, y, width, h);
            y += h + Sc(8);
        }

        _ask.Visible = _ask.Text.Length > 0;
        var promptHeight = _prompt.Measure(width).Height;
        _prompt.SetBounds(0, y, width, promptHeight);
        y += promptHeight + Sc(14);
        if (_answer.Visible)
        {
            var h = _answer.Measure(width - Sc(24)).Height;
            _answer.SetBounds(Sc(12), y + Sc(10), width - Sc(24), h);
            _answerBox = new Rectangle(0, y, width, h + Sc(20));
            y += h + Sc(20) + Sc(14);
        }
        else
        {
            _answerBox = Rectangle.Empty;
        }

        var x = 0;
        var buttonHeight = Sc(32);
        foreach (var button in new[] { _reveal, _forgot, _remembered, _again })
        {
            if (!button.Visible)
                continue;
            var w = Math.Max(Sc(96), button.PreferredWidth(Sc(18)));
            button.SetBounds(x, y, w, buttonHeight);
            x += w + Sc(10);
        }

        if (x > 0)
            y += buttonHeight;
        Invalidate();
        return y;
    }

    Rectangle _answerBox;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(P.Group);
        if (_answerBox.IsEmpty)
            return;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(_answerBox, Sc(8));
        using var fill = new SolidBrush(P.Fill);
        g.FillPath(fill, path);
    }
}
