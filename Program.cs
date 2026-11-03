using System.Runtime.InteropServices;

namespace Vibelish;

static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string? className, string? window);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    static int HookSelfTest()
    {
        var dll = ImeCapture.DllPath();
        using var capture = new ImeCapture(dll);
        capture.Install();
        if (!capture.Installed)
        {
            Console.WriteLine(capture.Error);
            return 2;
        }

        // 以前往记事本发字，Windows 11 的新记事本是打包应用，拿不到 Edit 窗口，测试总失败。
        // 改成另开一个自己的小窗口当目标。
        capture.BeginSession();
        var target = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--hook-target") { UseShellExecute = false });
        if (target == null)
        {
            Console.WriteLine("NO_TARGET");
            return 3;
        }

        try
        {
            target.WaitForInputIdle(5000);
            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 50 && hwnd == IntPtr.Zero; i++)
            {
                target.Refresh();
                hwnd = target.MainWindowHandle;
                if (hwnd == IntPtr.Zero) Thread.Sleep(100);
            }

            var edit = FindWindowEx(hwnd, IntPtr.Zero, null, null);
            if (edit == IntPtr.Zero) edit = hwnd;
            _ = SetForegroundWindow(hwnd);
            Thread.Sleep(300);
            if (GetForegroundWindow() != hwnd)
            {
                // 钩子只记前台进程里的输入；从后台启动的测试抢不到前台。
                Console.WriteLine("NOT_FOREGROUND（请在前台的终端里运行）");
                return 4;
            }

            PostMessage(edit, 0x0102, (IntPtr)'你', IntPtr.Zero);
            PostMessage(edit, 0x0102, (IntPtr)'好', IntPtr.Zero);
            Thread.Sleep(600);
            var text = capture.Read().Committed;
            Console.WriteLine("GOT=" + text);
            return text.Contains('你') && text.Contains('好') ? 0 : 1;
        }
        finally
        {
            try { target.Kill(); } catch { }
        }
    }

    /// <summary>--selftest-hook 的目标窗口：一个文本框，15 秒后自己关掉。</summary>
    static int HookTarget()
    {
        Application.EnableVisualStyles();
        using var form = new Form
        {
            Text = "Vibelish hook test",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(360, 120),
            TopMost = true,
        };
        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill };
        form.Controls.Add(box);
        form.Shown += (_, _) =>
        {
            form.Activate();
            box.Focus();
        };
        var close = new System.Windows.Forms.Timer { Interval = 15000 };
        close.Tick += (_, _) => form.Close();
        close.Start();
        Application.Run(form);
        return 0;
    }

    static string? CardSelfTest()
    {
        using var form = new OverlayForm();
        form.BeginListening("考研", "请开始输入中文…");
        form.UpdateSource("政策已经落实了。", Rectangle.Empty);
        form.ShowTranslation("政策已经落实了。", "The policy was implemented.", "test");
        if (form.ResultText != "The policy was implemented.")
            return "english";
        form.UpdateSource("政策已经落实了吗？", Rectangle.Empty);
        if (form.ResultText.Length != 0)
            return "stale english should not be copyable";
        form.UpdateSource("政策已经落实了。", Rectangle.Empty);
        if (form.ResultText != "The policy was implemented.")
            return "english restored";
        var before = form.Height;
        form.BeginWordLookup("implemented");
        if (!form.GlossOpen || form.GlossWord != "implemented")
            return "loading";
        if (form.Height <= before)
            return "loading height " + before + " -> " + form.Height;

        form.ShowWordGloss(new WordGloss(
            true,
            "implemented",
            "implement",
            "/x/",
            new[]
            {
                new WordSense("v.", "实施", true, "本句是落实"),
                new WordSense("n.", "工具", false, "另一义"),
            },
            "做完",
            "",
            true,
            ""));
        if (form.SelectedSense?.Chinese != "实施")
            return "selected";
        form.ShowRewrite("They carried it out.", "他们落实了。");
        if (form.RewriteEnglish != "They carried it out.")
            return "rewrite";
        var openHeight = form.Height;
        form.CloseGloss();
        if (form.GlossOpen || form.Height >= openHeight)
            return "close " + openHeight + " -> " + form.Height;
        form.ShowTranslationError("接口超时");
        form.FinishSession();
        if (form.ResultText != "The policy was implemented.")
            return "finish keeps english";

        // 逐字输出时还不能复制，结束后才行。
        form.BeginListening("考研", "");
        form.SetTranslating(true);
        form.ShowPartial("政策已经落实了。", "The policy");
        if (form.ResultText.Length != 0)
            return "streaming english should not be copyable";
        form.ShowTranslation("政策已经落实了。", "The policy was implemented.", "test");
        form.SetTranslating(false);
        if (form.ResultText != "The policy was implemented.")
            return "english after stream";

        if (Translator.VisiblePart("<think>a</think>Policy is <<<WO") != "Policy is")
            return "visible part marker prefix";
        if (Translator.VisiblePart("<think>still thinking") != "")
            return "visible part open think";
        var (english, words, noBetter) = Translator.SplitWords("It was implemented.\n<<< WORDS >>>\n{\"words\":[{\"en\":\"implement\",\"zh\":\"实施\",\"why\":\"正式\"}]}");
        if (english != "It was implemented." || words.Count != 1 || words[0].English != "implement" || words[0].Chinese != "实施" || noBetter != null)
            return "split words " + english + " / " + words.Count;
        var (chinese, _, better) = Translator.SplitWords("我昨天去了图书馆。\n<<<WORDS>>>\n{\"better\":\"I went to the library yesterday.\",\"words\":[]}");
        if (chinese != "我昨天去了图书馆。" || better != "I went to the library yesterday.")
            return "split better " + chinese + " / " + better;
        if (Translator.SplitWords("很好。\n<<<WORDS>>>\n{\"better\":\"\"}").Better != "")
            return "split better empty";

        // 翻译方向：自动时有中文就中译英，纯英文就英译中；太短的、只有数字的不翻。
        var auto = new AppConfig().Normalize();
        if (auto.ToNative("帮我 fix 这个 bug") || !auto.ToNative("fix the login bug"))
            return "auto direction";
        if (!auto.Translatable("fix the bug") || auto.Translatable("ok") || auto.Translatable("v2 12") || !auto.Translatable("你好"))
            return "translatable";
        var zh = new AppConfig { Direction = "forward" }.Normalize();
        if (zh.Translatable("fix the bug") || zh.Readable("fix the bug"))
            return "forward ignores english";
        var en = new AppConfig { Direction = "reverse" }.Normalize();
        if (!en.ToNative("帮我 fix 这个 bug") || en.Readable("你好"))
            return "reverse";
        if (new AppConfig { Direction = "zh2en" }.Normalize().Direction != "forward" || new AppConfig { Direction = "en2zh" }.Normalize().Direction != "reverse")
            return "legacy direction";
        if (!Lang.SameSentence("I went there.", "i went there") || Lang.SameSentence("I went there.", "I go there."))
            return "same sentence";

        // 别的语言对：看文字（假名、谚文、泰文、西里尔字母），都是拉丁字母时看常用词。
        var failure = PairCheck("zh-CN", "ja", "明日は会議があります", "明天要开会")
            ?? PairCheck("zh-CN", "ko", "저는 학생입니다", "我是学生")
            ?? PairCheck("zh-CN", "ru", "Я учусь в университете", "我在上大学")
            ?? PairCheck("zh-CN", "th", "สวัสดีครับ ยินดีที่ได้รู้จัก", "很高兴认识你")
            ?? PairCheck("zh-CN", "fr", "Je suis très content de te voir", "很高兴见到你")
            ?? PairCheck("ja", "en", "I would like a cup of coffee", "コーヒーを一杯ください")
            ?? PairCheck("ja", "zh-CN", "我想喝咖啡", "コーヒーが飲みたいです")
            ?? PairCheck("es", "en", "the weather is nice today", "el tiempo está muy bueno hoy")
            ?? PairCheck("en", "fr", "je suis très content de te voir", "I am glad to see you")
            ?? PairCheck("en", "de", "ich bin müde und will nach Hause", "I am tired and want to go home");
        if (failure != null)
            return failure;
        var same = new AppConfig { NativeLanguage = "ja", TargetLanguage = "JA" }.Normalize();
        if (same.NativeLanguage != "ja" || same.TargetLanguage != "en")
            return "same pair " + same.TargetLanguage;
        var ja = new AppConfig { NativeLanguage = "zh-CN", TargetLanguage = "ja", Direction = "forward" }.Normalize();
        if (ja.DirectionLabel != "中→日" || ja.LevelLabel != "高级" || AppConfig.PairLabel(ja.Target, ja.Native) != "日译中")
            return "pair labels " + ja.DirectionLabel + " " + ja.LevelLabel;
        if (new AppConfig().Normalize().LevelLabel != "考研" || new AppConfig { NativeLanguage = "xx" }.Normalize().NativeLanguage != "zh-CN")
            return "default pair";
        var (french, frenchWords, _) = Translator.SplitWords("Je suis content.\n<<<WORDS>>>\n{\"words\":[{\"word\":\"content\",\"meaning\":\"高兴的\",\"why\":\"常用\"}]}");
        if (french != "Je suis content." || frenchWords.Count != 1 || frenchWords[0].English != "content" || frenchWords[0].Chinese != "高兴的")
            return "split words other pair";

        // 英译中：译文是中文，查词、朗读、写入用的是更地道的英文。
        form.BeginListening("英→中", "");
        form.UpdateSource("I go to library yesterday.", Rectangle.Empty, sourceIsTarget: true);
        form.ShowTranslation("I go to library yesterday.", "我昨天去了图书馆。", "test", toNative: true, better: "I went to the library yesterday.");
        if (!form.ToNative || form.ResultText != "我昨天去了图书馆。")
            return "reverse result";
        if (form.WriteText != "I went to the library yesterday." || form.LookupSentence != "I went to the library yesterday.")
            return "reverse better";
        form.ShowTranslation("I went to the library yesterday.", "我昨天去了图书馆。", "test", toNative: true, better: "");
        if (form.WriteText.Length != 0 || form.LookupSentence != "I went to the library yesterday.")
            return "reverse already good";
        form.AdoptBetter("Yesterday I went to the library.");
        if (form.WriteText != "Yesterday I went to the library.")
            return "adopt better";
        form.ShowTranslation("政策已经落实了。", "The policy was implemented.", "test");
        if (form.ToNative || form.WriteText != "The policy was implemented.")
            return "back to english";

        // 中文译文要能在字与字之间换行，不能一整句挤在一行里。
        using (var cjk = new Font("Microsoft YaHei UI", 14F))
        {
            var layout = new EnglishLayout();
            layout.Build("虽然准备考研很辛苦，但我相信坚持下去一定会有收获，所以每天都去图书馆复习英语。", cjk, 200);
            if (layout.Width > 200 || layout.Height < cjk.Height * 2)
                return "cjk wrap " + layout.Width + "x" + layout.Height;
            if (layout.Runs.Any(run => run.Bounds.Left == 0 && run.Text is "，" or "。" && run.Bounds.Top > 0))
                return "cjk punctuation at line start";
        }

        // 泰文词和词之间不空格，也要能换行；声调、元音符号不能和前面的字拆开。
        using (var thai = new Font("Leelawadee UI", 14F))
        {
            var layout = new EnglishLayout();
            layout.Build("ฉันอยากไปห้องสมุดเพื่อทบทวนภาษาอังกฤษทุกวันเพราะการสอบใกล้เข้ามาแล้ว", thai, 200);
            if (layout.Width > 200 || layout.Height < thai.Height * 2)
                return "thai wrap " + layout.Width + "x" + layout.Height;
            if (layout.Runs.Any(run => run.Text.Length > 0 && char.GetUnicodeCategory(run.Text[0]) == System.Globalization.UnicodeCategory.NonSpacingMark))
                return "thai mark split";
        }

        if (!HotkeySpec.TryParse("ctrl+alt+t", out var spec) || spec.ToString() != "Ctrl+Alt+T")
            return "hotkey parse";
        if (HotkeySpec.TryParse("Shift+A", out _) || HotkeySpec.TryParse("Ctrl+Alt", out _))
            return "hotkey invalid accepted";
        if (!HotkeySpec.TryParse("Ctrl+Shift+F9", out var f9) || f9.Key != Keys.F9)
            return "hotkey f9";
        if (Translator.CleanModelText("<think>hmm</think>\n\"Hello.\"") != "Hello.")
            return "clean model text";
        if (!AppConfig.IsLocalEndpoint("http://192.168.1.40/v1") || AppConfig.IsLocalEndpoint("https://api.deepseek.com/v1"))
            return "local endpoint";
        return null;
    }

    /// <summary>自动方向下：在学的语言要判成反向，母语要判成正向，两边都够翻。</summary>
    static string? PairCheck(string native, string target, string targetText, string nativeText)
    {
        var config = new AppConfig { NativeLanguage = native, TargetLanguage = target }.Normalize();
        if (!config.ToNative(targetText) || config.ToNative(nativeText))
            return "pair " + native + ">" + target + " direction";
        if (!config.Translatable(targetText) || !config.Translatable(nativeText))
            return "pair " + native + ">" + target + " translatable";
        return null;
    }

    /// <summary>「zh-CN>ja」或「zh-CN>ja@reverse」：临时换语言对（和方向），排查、截图用，不改配置文件。</summary>
    static bool TryApplyPair(AppConfig config, string arg)
    {
        var at = arg.IndexOf('@');
        var pair = at >= 0 ? arg[..at] : arg;
        var arrow = pair.IndexOf('>');
        if (arrow <= 0)
            return false;
        config.NativeLanguage = pair[..arrow];
        config.TargetLanguage = pair[(arrow + 1)..];
        if (at >= 0)
            config.Direction = arg[(at + 1)..];
        return true;
    }

    /// <summary>不启动托盘，直接把卡片或设置窗口摆出来，方便截图检查界面。
    /// 给了 shot 路径就在显示后截一张图存下来再退出。</summary>
    static int Preview(string what, string theme, string? shot, string? pair)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // theme 可以带强调色，比如 dark+purple。
        var parts = theme.Split('+');
        var config = AppConfig.Load();
        config.Accent = parts.Length > 1 ? parts[1] : config.Accent;
        // ja、fr 两张示例卡片用自己的语言对；别的可以用 pair 参数换，比如 zh-CN>ko。
        if (what is "ja" or "fr")
            pair = "zh-CN>" + what;
        if (pair != null)
            TryApplyPair(config, pair);
        config = config.Normalize();
        Theme.Apply(parts[0], "normal", config.Accent);
        if (what.StartsWith("settings", StringComparison.Ordinal))
        {
            // settings 或 settings:2（直接打开第 3 页）。
            var page = what.Length > 9 && int.TryParse(what[9..], out var index) ? index : 0;
            using var settings = new SettingsForm(config, "正在使用 " + config.Hotkey + "。");
            settings.Shown += (_, _) => settings.ShowPage(page);
            settings.Saved += (next, _) =>
            {
                // 预览里也能实时看换主题、换强调色的效果，但不写配置文件。
                Theme.Apply(next.Theme, next.CardTextSize, next.Accent);
                settings.ApplyTheme();
            };
            if (shot != null)
                CaptureLater(settings, shot);
            Application.Run(settings);
            return 0;
        }

        using var card = new OverlayForm();
        config.StudyStyle = what switch
        {
            "detail" => "detail",
            "words" => "word",
            _ => "meaning",
        };
        card.ApplyOptions(config);
        card.ApplyTheme();
        var close = new System.Windows.Forms.Timer { Interval = 60000 };
        close.Tick += (_, _) => Application.ExitThread();
        close.Start();
        if (shot != null)
            CaptureLater(card, shot);
        card.CloseRequested += (_, _) => Application.ExitThread();
        var listen = config.ArrowLabel(false) + " · " + config.LevelLabel;
        var hint = "请开始输入" + config.Native.Name + "或" + config.Target.Name + "…  Esc 取消";
        switch (what)
        {
            case "idle":
                card.ShowIdle("Vibelish", "按 Ctrl+Alt+T 开始，输入" + config.Native.Name + "或" + config.Target.Name + "，按 Enter 完成", OverlayForm.CardPlace.TopCenter);
                break;
            case "ja":
                // 中译日：译文、重点词用日文字体。
                card.BeginListening(listen, hint);
                card.UpdateSource("我们明天下午三点开会，请大家准时参加。", Rectangle.Empty);
                card.ShowTranslation(
                    "我们明天下午三点开会，请大家准时参加。",
                    "明日の午後三時から会議を行いますので、皆様時間通りにご出席ください。",
                    "deepseek-chat · 高级");
                card.ShowStudy(new[]
                {
                    new StudyWord("会議を行う", "召开会议", "比「会議がある」更正式"),
                    new StudyWord("時間通りに", "准时", "固定说法"),
                    new StudyWord("ご出席ください", "请出席", "敬语，比「来てください」礼貌"),
                });
                card.SetStatus("已复制日文", OverlayForm.StatusKind.Success);
                break;
            case "fr":
                // 法译中：自己写的法文 → 中文意思 + 更地道的说法。
                const string french = "je veux aller a la bibliotheque demain pour etudier le francais";
                card.BeginListening(config.ArrowLabel(true) + " · " + config.LevelLabel, hint);
                card.UpdateSource(french, Rectangle.Empty, sourceIsTarget: true);
                card.ShowTranslation(
                    french,
                    "我明天想去图书馆学法语。",
                    "deepseek-chat · 法译中",
                    toNative: true,
                    better: "Demain, je compte me rendre à la bibliothèque afin d'étudier le français.");
                card.ShowStudy(new[]
                {
                    new StudyWord("compter faire", "打算做", "比 vouloir 更书面"),
                    new StudyWord("se rendre à", "前往", "比 aller à 更正式"),
                    new StudyWord("afin de", "为了", "替代 pour，书面语"),
                });
                break;
            case "reverse":
            case "reverse-good":
                // 英译中：自己写的英文 → 中文意思 + 更地道的说法。
                var good = what == "reverse-good";
                var mine = good ? "Could you help me refactor this function so that it is easier to test?" : "help me fix this bug, the login page always crash when user click the button";
                card.BeginListening(config.ArrowLabel(true) + " · " + config.LevelLabel, hint);
                card.UpdateSource(mine, Rectangle.Empty, sourceIsTarget: true);
                card.ShowTranslation(
                    mine,
                    good ? "你能帮我重构一下这个函数，让它更容易测试吗？" : "帮我修复这个 bug：用户点击按钮时，登录页面总是崩溃。",
                    "deepseek-chat · 英译中",
                    toNative: true,
                    better: good ? "" : "Please help me fix this bug: the login page invariably crashes whenever a user clicks the button.");
                card.ShowStudy(good
                    ? new[] { new StudyWord("refactor", "重构", "编程常用词") }
                    : new[]
                    {
                        new StudyWord("invariably", "总是；一贯", "比 always 更书面"),
                        new StudyWord("crashes", "崩溃", "第三人称单数要加 -es"),
                        new StudyWord("whenever", "每当", "替代 when，强调每次"),
                    });
                break;
            case "listening":
                card.BeginListening(listen, hint);
                card.UpdateSource("我今天想去图书馆复习英语", Rectangle.Empty);
                card.SetTranslating(true);
                break;
            case "stream":
                card.BeginListening(listen, hint);
                card.UpdateSource("虽然准备考研很辛苦，但我相信坚持下去一定会有收获。", Rectangle.Empty);
                card.SetTranslating(true);
                card.ShowPartial("虽然准备考研很辛苦，但我相信坚持下去一定会有收获。", "Arduous as preparation for the postgraduate entrance examination may be, I remain");
                break;
            case "error":
                card.BeginListening(listen, hint);
                card.UpdateSource("我今天想去图书馆复习英语", Rectangle.Empty);
                card.ShowTranslationError("接口返回 401（密钥不对或没有权限）：Incorrect API key provided");
                break;
            default:
                card.BeginListening(listen, hint);
                card.ShowTranslation(
                    "虽然准备考研很辛苦，但我相信坚持下去一定会有收获。",
                    "Arduous as preparation for the postgraduate entrance examination may be, I remain convinced that perseverance will ultimately yield its rewards.",
                    "deepseek-chat · 考研");
                card.ShowStudy(new[]
                {
                    new StudyWord("arduous", "艰辛的", "比 hard 更书面"),
                    new StudyWord("remain convinced", "始终相信", "替代 believe"),
                    new StudyWord("yield", "带来", "yield rewards 固定搭配"),
                });
                if (what == "gloss")
                {
                    card.BeginWordLookup("yield");
                    card.ShowWordGloss(new WordGloss(true, "yield", "yield", "/jiːld/",
                        new[]
                        {
                            new WordSense("v.", "产生；带来（结果、收益）", true, "这里指坚持最终会带来回报。"),
                            new WordSense("v.", "屈服；让步", false, "yield to pressure 屈服于压力"),
                            new WordSense("n.", "产量；收益", false, "crop yield 作物产量"),
                        },
                        "yield = 给出，引申为产出、让出", "", true, ""));
                }

                card.SetStatus("已复制" + config.Target.Name, OverlayForm.StatusKind.Success);
                break;
        }

        Application.Run();
        return 0;
    }

    static void CaptureLater(Form form, string path)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                // 直接让窗口画到位图上：屏幕截图在窗口被挡住、或者桌面在别的会话时是黑的。
                using var bitmap = new Bitmap(form.Width, form.Height);
                using (var g = Graphics.FromImage(bitmap))
                {
                    var dc = g.GetHdc();
                    try
                    {
                        PrintWindow(form.Handle, dc, PwRenderFullContent);
                    }
                    finally
                    {
                        g.ReleaseHdc(dc);
                    }
                }

                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            finally
            {
                Application.ExitThread();
            }
        };
        timer.Start();
    }

    const int AttachParentProcess = -1;
    const uint PwRenderFullContent = 2;

    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest-terminal")
        {
            _ = AttachConsole(AttachParentProcess);
            var failure = TerminalReader.SelfTest();
            Console.WriteLine(failure == null ? "ok" : "FAIL " + failure);
            return failure == null ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == "--selftest-words")
        {
            _ = AttachConsole(AttachParentProcess);
            var failure = WordStudy.SelfTest() ?? CardSelfTest();
            Console.WriteLine(failure == null ? "ok" : "FAIL " + failure);
            return failure == null ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == "--selftest-speech")
        {
            // 只检查能不能找到语音，不出声。默认查英文，也可以给一个文化名，比如 ja-JP。
            _ = AttachConsole(AttachParentProcess);
            var culture = args.Length >= 2 ? args[1] : "en-US";
            var voice = Speech.HasVoice(culture);
            Console.WriteLine(voice ? "ok " + culture : "FAIL " + Speech.MissingHint(culture));
            Speech.Dispose();
            return voice ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == "--hook-target")
            return HookTarget();

        if (args.Length >= 1 && args[0] == "--selftest-hook")
        {
            _ = AttachConsole(AttachParentProcess);
            return HookSelfTest();
        }

        if (args.Length >= 1 && args[0] == "--ocr-wechat")
        {
            _ = AttachConsole(AttachParentProcess);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var text = OcrReader.ProbeWeChatAsync().GetAwaiter().GetResult();
            Console.WriteLine(text);
            return 0;
        }

        if (args.Length >= 1 && args[0] == "--probe-focus")
        {
            _ = AttachConsole(AttachParentProcess);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var probePath = Path.Combine(Path.GetTempPath(), "vibelish-focus.txt");
            FocusProbe.Dump(probePath);
            Console.WriteLine("WROTE " + probePath);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--diag-hwnd")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var hwnd = new IntPtr(long.Parse(args[1]));
            var element = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var ok = TerminalReader.TryRead(element, out var text, out var bounds);
            ConsoleBuffer.Release();
            _ = AttachConsole(AttachParentProcess);
            Console.WriteLine("try=" + ok + " len=" + text.Length + " bounds=" + bounds);
            var preview = text.Length > 120 ? text[..120] : text;
            Console.WriteLine("text=" + preview.Replace("\n", "\\n"));
            return ok ? 0 : 1;
        }

        if (args.Length >= 2 && args[0] == "--read-pid")
        {
            var pid = uint.Parse(args[1]);
            var ok = ConsoleBuffer.TryReadRows(pid, out var rows, out var title, out var error, out var cursor);
            var text = ok ? TerminalReader.ReadScreenTail(rows, cursor) : "";
            ConsoleBuffer.Release();
            _ = AttachConsole(AttachParentProcess);
            Console.WriteLine("ok=" + ok + " err=" + error + " title=" + title);
            Console.WriteLine("text=" + text.Replace("\n", "\\n"));
            foreach (var row in rows)
                Console.WriteLine("row=" + row);
            return ok ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == "--diag")
        {
            _ = AttachConsole(AttachParentProcess);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var watcher = new FieldWatcher(Environment.ProcessId);
            watcher.SetEnabled(true);
            Thread.Sleep(900);
            var snapshot = watcher.Latest ?? FocusSnapshot.Fail("后台读取超时");
            Console.WriteLine("ok=" + snapshot.Ok + " reason=" + snapshot.Reason);
            try
            {
                var probe = System.Windows.Automation.AutomationElement.FocusedElement;
                Console.WriteLine("uia=ok " + (probe == null ? "null" : probe.Current.ControlType.ProgrammaticName));
            }
            catch (Exception ex)
            {
                Console.WriteLine("uia=error " + ex);
            }
            if (snapshot.Ok)
            {
                Console.WriteLine("app=" + snapshot.App + " window=" + snapshot.Window + " control=" + snapshot.Control);
                Console.WriteLine("len=" + snapshot.Paragraph.Length
                    + " whole=" + snapshot.WholeField
                    + " write=" + snapshot.CanWrite
                    + " ocr=" + snapshot.NeedsOcr
                    + " bounds=" + snapshot.Bounds);
                if (args.Length >= 2 && args[1] == "--text")
                {
                    var preview = snapshot.Paragraph.Length > 120 ? snapshot.Paragraph[..120] : snapshot.Paragraph;
                    Console.WriteLine("text=" + preview.Replace("\n", "\\n"));
                }
            }

            return 0;
        }

        if (args.Length >= 1 && args[0] == "--translate")
        {
            _ = AttachConsole(AttachParentProcess);
            if (args.Length < 2)
            {
                Console.WriteLine("用法：Vibelish.exe --translate 要翻译的文字 [母语>在学的语言[@方向]] [引擎] [接口地址 模型]");
                return 2;
            }

            // 可以临时换语言对，比如 zh-CN>ja 或 zh-CN>ja@reverse。
            var probe = AppConfig.Load();
            var rest = new List<string>();
            for (var i = 2; i < args.Length; i++)
            {
                if (!TryApplyPair(probe, args[i]))
                    rest.Add(args[i]);
            }

            // 剩下的第一个可以临时换引擎：auto / openai / transmart / mymemory / google。
            if (rest.Count >= 1)
                probe.Engine = rest[0];
            // 再往后是接口地址和模型，排查用，不改配置文件。
            if (rest.Count >= 3)
            {
                probe.OpenAiBaseUrl = rest[1];
                probe.OpenAiModel = rest[2];
            }
            probe = probe.Normalize();
            var translator = new Translator(probe);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long first = 0;
            var result = translator.TranslateAsync(args[1], CancellationToken.None, _ =>
            {
                if (first == 0)
                    first = watch.ElapsedMilliseconds;
            }).GetAwaiter().GetResult();
            Console.WriteLine(result.Ok ? result.Text : "ERROR " + result.Detail);
            if (result.Better is { Length: > 0 } better)
                Console.WriteLine("better=" + better);
            Console.WriteLine("engine=" + result.Detail + " pair=" + probe.NativeLanguage + ">" + probe.TargetLanguage
                + " dir=" + (result.ToNative ? "reverse" : "forward")
                + " first=" + (result.FirstMs > 0 ? result.FirstMs : first) + "ms total=" + watch.ElapsedMilliseconds + "ms"
                + " words=" + (result.Words?.Count ?? 0));
            return result.Ok ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == "--preview")
            return Preview(
                args.Length >= 2 ? args[1] : "card",
                args.Length >= 3 ? args[2] : "system",
                args.Length >= 4 && args[3] != "-" ? args[3] : null,
                args.Length >= 5 ? args[4] : null);

        if (args.Length >= 1 && args[0] == "--uninstall")
        {
            _ = AttachConsole(AttachParentProcess);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            var quiet = args.Any(a => string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase));
            _ = Uninstaller.ConfirmAndRun(quiet);
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\Vibelish.Single", out var created);
        if (!created)
        {
            // Ask the running instance to show its card instead of failing silently.
            try
            {
                using var show = EventWaitHandle.OpenExisting(@"Local\Vibelish.Show");
                show.Set();
            }
            catch
            {
                // The first instance may still be starting; nothing else to do.
            }

            TraceLog.Note("already-running");
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => TraceLog.Note("ui " + TraceLog.Safe(eventArgs.Exception.ToString()));
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => TraceLog.Note("fatal " + TraceLog.Safe(eventArgs.ExceptionObject.ToString() ?? ""));
        var autostart = args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));
        try
        {
            TraceLog.Note(autostart ? "start(autostart)" : "start");
            Application.Run(new TrayApplicationContext(autostart));
            TraceLog.Note("exit");
        }
        catch (Exception ex)
        {
            TraceLog.Note("fatal " + TraceLog.Safe(ex.ToString()));
            throw;
        }

        return 0;
    }
}

static class TraceLog
{
    const long MaxBytes = 1024 * 1024;
    static readonly object Gate = new();

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibelish", "run.log");

    /// <summary>只记事件和长度，不记原文。超过 1 MB 换成 run.old.log。</summary>
    public static void Note(string line)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, Path.Combine(info.DirectoryName!, "run.old.log"), true);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must not take down the tray app.
        }
    }

    public static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        text = TerminalReader.RedactSecrets(text);
        var cut = text.IndexOf("sk-", StringComparison.Ordinal);
        if (cut >= 0)
            text = text[..cut] + "sk-redacted";
        return text.Length > 500 ? text[..500] : text;
    }
}
