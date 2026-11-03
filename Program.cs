using System.Runtime.InteropServices;

namespace InputTranslate;

static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string? className, string? window);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

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

        capture.BeginSession();
        var note = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        if (note == null)
        {
            Console.WriteLine("NO_NOTEPAD");
            return 3;
        }

        note.WaitForInputIdle(4000);
        var hwnd = IntPtr.Zero;
        for (var i = 0; i < 30 && hwnd == IntPtr.Zero; i++)
        {
            note.Refresh();
            hwnd = note.MainWindowHandle;
            if (hwnd == IntPtr.Zero) Thread.Sleep(100);
        }

        var edit = FindWindowEx(hwnd, IntPtr.Zero, "Edit", null);
        if (edit == IntPtr.Zero) edit = hwnd;
        _ = SetForegroundWindow(hwnd);
        Thread.Sleep(200);
        PostMessage(edit, 0x0102, (IntPtr)'你', IntPtr.Zero);
        PostMessage(edit, 0x0102, (IntPtr)'好', IntPtr.Zero);
        Thread.Sleep(600);
        var text = capture.Read().Committed;
        Console.WriteLine("GOT=" + text);
        try { note.Kill(); } catch { }
        return text.Contains('你') && text.Contains('好') ? 0 : 1;
    }

    static string? CardSelfTest()
    {
        using var form = new OverlayForm();
        form.ShowEnglish("The policy was implemented.", "");
        if (form.EnglishText != "The policy was implemented.")
            return "english";
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
        return null;
    }

    const int AttachParentProcess = -1;

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
            var probePath = Path.Combine(Path.GetTempPath(), "input-translate-focus.txt");
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
            var translator = new Translator(AppConfig.Load());
            var result = translator.TranslateAsync(args[1], CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(result.Ok ? result.Text : "ERROR " + result.Detail);
            return result.Ok ? 0 : 1;
        }

        if (args.Length >= 1 && args[0] == "--uninstall")
        {
            _ = AttachConsole(AttachParentProcess);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            var quiet = args.Any(a => string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase));
            _ = Uninstaller.ConfirmAndRun(quiet);
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\InputTranslate.Single", out var created);
        if (!created)
        {
            // Ask the running instance to show its card instead of failing silently.
            try
            {
                using var show = EventWaitHandle.OpenExisting(@"Local\InputTranslate.Show");
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
    public static void Note(string line)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InputTranslate", "run.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
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
        var cut = text.IndexOf("sk-", StringComparison.Ordinal);
        if (cut >= 0)
            text = text[..cut] + "sk-redacted";
        return text.Length > 500 ? text[..500] : text;
    }
}
