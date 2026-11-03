using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Vibelish;

/// <summary>
/// 读取 Windows Terminal、经典控制台，以及 VS Code / Cursor 集成终端里光标所在的输入行。
/// 终端文本范围的 UI Automation 调用会触发访问冲突，正文改从控制台屏幕缓冲读取。
/// </summary>
static class TerminalReader
{
    const int MaxChars = 1500;

    static readonly Regex ShellPrompt = new(
        @"^(?:PS>\s*|PS\s+[^>\r\n]*>\s*|[A-Za-z]:\\[^>\r\n]*>\s*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex TuiPrompt = new(
        @"^\s*[>❯›]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex FancyPrompt = new(
        @"^[^\u4e00-\u9fff\r\n]{0,80}[❯›]\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex BoxEdge = new(
        @"^[\u2500-\u257F\s]+|[\u2500-\u257F\s]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex LineGutter = new(
        @"^(?:[│|]\s*)?(?<n>\d{1,3})\s*(?:[│|]\s*|\s{2,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex SecretToken = new(
        @"\b(?:ghp|gho|ghu|ghs|github_pat)_[A-Za-z0-9_]{16,}\b|\bsk-[A-Za-z0-9]{16,}\b|\bxai-[A-Za-z0-9]{16,}\b|\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex StatusTail = new(
        @"\s+(?:\d+m\d+s\b|\[\s*stop\s*\]|always-approve).*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryRead(AutomationElement element, out string text, out Rectangle bounds)
    {
        text = "";
        bounds = Rectangle.Empty;
        AutomationElement? surface;
        try
        {
            surface = Resolve(element);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (Exception ex) when (IsAutomationFault(ex))
        {
            return false;
        }

        if (surface == null)
            return TryReadIde(element, out text, out bounds);

        // 已经确认是终端。即使暂时读不到字也返回成功，避免再走会崩溃的文本范围。
        bounds = ElementBounds(surface);
        try
        {
            text = ReadConsole(surface);
        }
        catch (ElementNotAvailableException)
        {
            text = "";
        }
        catch (Exception ex) when (IsAutomationFault(ex))
        {
            text = "";
        }

        return true;
    }

    static IntPtr _cachedHwnd;
    static uint _cachedPid;
    static string _cachedTab = "";

    static string ReadConsole(AutomationElement surface)
    {
        var hwnd = Native.FindHwnd(surface);
        var tab = SelectedTabName(surface);
        if (hwnd == _cachedHwnd && _cachedPid != 0 && tab == _cachedTab
            && ConsoleBuffer.TryReadRows(_cachedPid, out var cached, out _, out _, out var cachedCursor))
            return ReadScreenTail(cached, cachedCursor);

        _cachedPid = 0;
        var root = ProcessId(surface);
        string? fallback = null;
        uint fallbackPid = 0;
        foreach (var pid in Candidates(root))
        {
            if (!ConsoleBuffer.TryReadRows(pid, out var rows, out var title, out _, out var cursor))
                continue;
            var line = ReadScreenTail(rows, cursor);
            if (tab.Length > 0 && TitlesMatch(title, tab))
            {
                _cachedHwnd = hwnd;
                _cachedPid = pid;
                _cachedTab = tab;
                return line;
            }

            if (fallback == null || (HasCjk(line) && !HasCjk(fallback)))
            {
                fallback = line;
                fallbackPid = pid;
            }
        }

        if (fallbackPid == 0)
            return "";
        _cachedHwnd = hwnd;
        _cachedPid = fallbackPid;
        _cachedTab = tab;
        return fallback ?? "";
    }

    static bool TitlesMatch(string title, string tab)
    {
        if (title.Length == 0 || tab.Length == 0)
            return false;
        return title.Contains(tab, StringComparison.OrdinalIgnoreCase)
            || tab.Contains(title, StringComparison.OrdinalIgnoreCase);
    }

    static int ProcessId(AutomationElement element)
    {
        try
        {
            return element.Current.ProcessId;
        }
        catch
        {
            return 0;
        }
    }

    static IEnumerable<uint> Candidates(int rootPid)
    {
        if (rootPid <= 0)
            yield break;

        var snapshot = ProcessSnapshot();
        var wanted = new List<int>();
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var proc in snapshot)
            {
                if (proc.Parent != parent || !seen.Add(proc.Pid))
                    continue;
                queue.Enqueue(proc.Pid);
                wanted.Add(proc.Pid);
            }
        }

        wanted.Add(rootPid);
        foreach (var pid in wanted.OrderBy(pid => Rank(ProcessNameOf(snapshot, pid))))
            yield return (uint)pid;
    }

    static int Rank(string name)
    {
        name = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wsl", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || name.Equals("grok", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node", StringComparison.OrdinalIgnoreCase)
            || name.Equals("python", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)
            || name.Equals("conhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
            return 3;
        return 2;
    }

    static string ProcessNameOf(List<Proc> snapshot, int pid)
    {
        foreach (var proc in snapshot)
        {
            if (proc.Pid == pid)
                return proc.Name;
        }

        return "";
    }

    static string SelectedTabName(AutomationElement element)
    {
        try
        {
            var window = element;
            for (var i = 0; i < 8; i++)
            {
                if (window.Current.ControlType == ControlType.Window)
                    break;
                var parent = TreeWalker.ControlViewWalker.GetParent(window);
                if (parent == null)
                    break;
                window = parent;
            }

            return FindSelectedTab(window, 0);
        }
        catch
        {
            return "";
        }
    }

    static string FindSelectedTab(AutomationElement element, int depth)
    {
        if (depth > 6)
            return "";
        try
        {
            if (element.Current.ControlType == ControlType.TabItem
                && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                && ((SelectionItemPattern)pattern).Current.IsSelected)
                return element.Current.Name ?? "";
        }
        catch
        {
            return "";
        }

        var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        var count = 0;
        while (child != null && count < 12)
        {
            var name = FindSelectedTab(child, depth + 1);
            if (name.Length > 0)
                return name;
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            count++;
        }

        return "";
    }

    readonly struct Proc
    {
        public int Pid { get; init; }
        public int Parent { get; init; }
        public string Name { get; init; }
    }

    static List<Proc> ProcessSnapshot()
    {
        var list = new List<Proc>();
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            return list;
        try
        {
            var entry = new ProcessEntry32 { dwSize = Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref entry))
                return list;
            do
            {
                list.Add(new Proc
                {
                    Pid = entry.th32ProcessID,
                    Parent = entry.th32ParentProcessID,
                    Name = entry.szExeFile ?? "",
                });
            }
            while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }

        return list;
    }

    const uint Th32csSnapProcess = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static string ReadScreenTail(IReadOnlyList<string> oldestFirst)
    {
        return ReadScreenTail(oldestFirst, oldestFirst.Count - 1);
    }

    public static string ReadScreenTail(IReadOnlyList<string> oldestFirst, int cursorIndex)
    {
        return RedactSecrets(Compose(SelectInputRows(oldestFirst, cursorIndex)));
    }

    public static string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return SecretToken.Replace(text, "[•••]");
    }

    internal static List<string> SelectInputRows(IReadOnlyList<string> oldestFirst, int cursorIndex)
    {
        var chosen = new List<string>();
        if (oldestFirst.Count == 0)
            return chosen;

        var cursor = Math.Clamp(cursorIndex, 0, oldestFirst.Count - 1);
        while (cursor > 0 && Normalize(oldestFirst[cursor]).Length == 0)
            cursor--;

        var current = Normalize(oldestFirst[cursor]);
        if (current.Length == 0)
            return chosen;

        var start = FindBlockStart(oldestFirst, cursor);
        for (var i = start; i <= cursor; i++)
        {
            var row = Normalize(oldestFirst[i]);
            if (row.Length == 0 || IsChrome(row))
                continue;
            chosen.Add(row);
        }

        // 只有确认这是一段输入（行首有提示符）时，才把光标下面的换行读进来。
        // 光标若停在状态栏上，上面的输入已经收过了，不能再往下吞界面。
        if (!IsChrome(current) && StartsInputRow(Normalize(oldestFirst[start])))
        {
            for (var i = cursor + 1; i < oldestFirst.Count && i <= cursor + 12; i++)
            {
                var row = Normalize(oldestFirst[i]);
                if (row.Length == 0 || IsChrome(row) || StartsInputRow(row))
                    break;
                chosen.Add(row);
            }
        }

        return chosen;
    }

    static int FindBlockStart(IReadOnlyList<string> rows, int cursor)
    {
        var current = Normalize(rows[cursor]);
        if (StartsInputRow(current))
            return cursor;

        for (var i = cursor - 1; i >= 0 && cursor - i <= 10; i--)
        {
            var row = Normalize(rows[i]);
            if (row.Length == 0 || IsChrome(row))
                break;
            if (StartsInputRow(row))
                return i;
        }

        if (cursor > 0)
        {
            var prev = Normalize(rows[cursor - 1]);
            if (StartsInputRow(prev))
                return cursor - 1;
            if (prev.Length >= 80 && cursor >= 2 && StartsInputRow(Normalize(rows[cursor - 2])))
                return cursor - 2;
        }

        return cursor;
    }

    internal static string Compose(IReadOnlyList<string> normalizedRows)
    {
        var bodies = new List<string>();
        var widths = new List<int>();
        foreach (var row in normalizedRows)
        {
            var body = StripGutter(StripPrompt(StripStatusTail(row)));
            if (body.Length == 0)
                continue;
            bodies.Add(body);
            widths.Add(row.Length);
        }

        if (bodies.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.Append(bodies[0]);
        for (var i = 1; i < bodies.Count; i++)
        {
            if (widths[i - 1] < 80)
                sb.Append('\n');
            sb.Append(bodies[i]);
        }

        var text = sb.ToString().Trim();
        if (text.Length > MaxChars)
            text = text[^MaxChars..].Trim();
        return text;
    }

    public static string? SelfTest()
    {
        var cases = new (string Name, string[] Rows, int Cursor, string Expect)[]
        {
            ("ps-path", ["PS E:\\workplaece\\Vibelish> 你好"], -1, "你好"),
            ("ps-short", ["PS> 你好"], -1, "你好"),
            ("cmd", ["C:\\Windows\\System32> 你好"], -1, "你好"),
            ("plain", ["今天天气不错"], -1, "今天天气不错"),
            ("tui", ["> 你好世界"], -1, "你好世界"),
            ("fancy", ["user@pc ~/src ❯ 帮我看看"], -1, "帮我看看"),
            ("box", ["│ > 请翻译这句话"], -1, "请翻译这句话"),
            ("ignore-history", ["上一行的输出", "PS C:\\> 你好"], -1, "你好"),
            ("ignore-chat", ["这是一段很长的助手回复" + new string('啊', 80), "你好"], -1, "你好"),
            ("wrap", ["PS C:\\> " + new string('文', 80), "还在继续"], -1, new string('文', 80) + "还在继续"),
            ("blank-caret", ["PS C:\\> 你好", ""], -1, "你好"),
            ("empty-prompt", ["PS C:\\Users\\foo> "], -1, ""),
            ("below-cursor", ["> 第一行港口沙盘", "步进电机也要上传。", "Grok 4.7 Fast · always-approve"], 0, "第一行港口沙盘\n步进电机也要上传。"),
            ("cursor-at-end", ["> 第一行港口沙盘", "2│步进电机也要上传。", "备注 ghp_aaaaaaaaaaaaaaaaaaaa"], 2, "第一行港口沙盘\n步进电机也要上传。\n备注 [•••]"),
            ("stop-blank", ["> 只要这一段", "", "下面是别的输出"], 0, "只要这一段"),
            ("no-swallow", ["你好", "下面还有一段"], 0, "你好"),
            ("cursor-on-status", ["> 港口沙盘", "步进电机", "Grok 4.7 · always-approve"], 2, "港口沙盘\n步进电机"),
        };

        foreach (var item in cases)
        {
            var cursor = item.Cursor < 0 ? item.Rows.Length - 1 : item.Cursor;
            var actual = ReadScreenTail(item.Rows, cursor);
            if (!string.Equals(actual, item.Expect, StringComparison.Ordinal))
                return item.Name + " => [" + actual + "] expect [" + item.Expect + "]";
        }

        return null;
    }

    static AutomationElement? Resolve(AutomationElement element)
    {
        if (IsTermControl(element) || IsConsoleDocument(element))
            return element;
        if (!IsHostWindow(element))
            return null;

        // XAML 的 ClassName 用 FindFirst 条件经常匹配不到，改成顺着控件树找。
        return FindSurface(element, 0);
    }

    static AutomationElement? FindSurface(AutomationElement element, int depth)
    {
        if (depth > 8)
            return null;
        if (depth > 0 && (IsTermControl(element) || IsConsoleDocument(element)))
            return element;

        var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        var count = 0;
        while (child != null && count < 20)
        {
            var found = FindSurface(child, depth + 1);
            if (found != null)
                return found;
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            count++;
        }

        return null;
    }

    static bool IsTermControl(AutomationElement element)
    {
        var cls = ClassName(element);
        if (cls.Contains("TermControl", StringComparison.OrdinalIgnoreCase))
            return true;
        return element.Current.ControlType == ControlType.Text
            && cls.Length == 0
            && HasTextPattern(element)
            && HostClass(element) == "CASCADIA_HOSTING_WINDOW_CLASS";
    }

    static bool IsConsoleDocument(AutomationElement element)
    {
        if (element.Current.ControlType != ControlType.Document || !HasTextPattern(element))
            return false;
        var name = element.Current.Name ?? "";
        if (name.Equals("Text Area", StringComparison.OrdinalIgnoreCase))
            return true;
        var host = HostClass(element);
        return host is "ConsoleWindowClass" or "PseudoConsoleWindow";
    }

    static bool IsHostWindow(AutomationElement element)
    {
        if (IsHostClass(ClassName(element)))
            return true;
        // 只有窗口元素才按窗口类名向下找终端，避免把同一 HWND 里的搜索框当成终端。
        if (element.Current.ControlType != ControlType.Window)
            return false;
        var hwnd = element.Current.NativeWindowHandle;
        if (hwnd == 0)
            return false;
        return IsHostClass(Win32Class(new IntPtr(hwnd)));
    }

    static bool IsHostClass(string cls)
    {
        return cls is "CASCADIA_HOSTING_WINDOW_CLASS" or "ConsoleWindowClass" or "PseudoConsoleWindow";
    }

    static string HostClass(AutomationElement element)
    {
        var hwnd = Native.FindHwnd(element);
        return hwnd == IntPtr.Zero ? "" : Win32Class(hwnd);
    }

    static string Win32Class(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string ClassName(AutomationElement element)
    {
        return element.Current.ClassName ?? "";
    }

    static bool HasTextPattern(AutomationElement element)
    {
        return element.TryGetCurrentPattern(TextPattern.Pattern, out _);
    }

    static bool TryReadIde(AutomationElement element, out string text, out Rectangle bounds)
    {
        text = "";
        bounds = Rectangle.Empty;
        try
        {
            if (!IsIdeTerminal(element))
                return false;

            var container = element;
            var parent = TreeWalker.ControlViewWalker.GetParent(element);
            if (parent != null && !IsHugeWindow(parent))
                container = parent;

            var lines = new List<string>();
            Harvest(container, lines, 0);
            if (lines.Count == 0)
            {
                var name = element.Current.Name ?? "";
                if (HasCjk(name))
                    lines.Add(name);
            }

            if (lines.Count == 0)
                return false;

            TrimStatus(lines);
            text = ReadScreenTail(lines.Count > 8 ? lines.GetRange(lines.Count - 8, 8) : lines);
            bounds = ElementBounds(element);
            if (bounds.IsEmpty)
                bounds = ElementBounds(container);
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (Exception ex) when (IsAutomationFault(ex))
        {
            return false;
        }
    }

    static bool IsIdeTerminal(AutomationElement element)
    {
        // 只认 VS Code / Cursor 终端输入框自己的辅助名称。
        // 父级标题里出现“终端”两个字不够，否则会把别的软件界面读进来。
        var name = element.Current.Name ?? "";
        if (name.IndexOf("terminal input", StringComparison.OrdinalIgnoreCase) < 0
            && name.IndexOf("终端输入", StringComparison.Ordinal) < 0)
            return false;
        var proc = ProcessName(element);
        return proc is "Code" or "Cursor" or "Windsurf" or "devenv" or "VSCodium" or "Code - Insiders";
    }

    static string ProcessName(AutomationElement element)
    {
        try
        {
            var pid = element.Current.ProcessId;
            if (pid <= 0)
                return "";
            return System.Diagnostics.Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return "";
        }
    }

    static bool IsHugeWindow(AutomationElement element)
    {
        var type = element.Current.ControlType;
        return type == ControlType.Window || (type == ControlType.Pane && (element.Current.Name ?? "").Length == 0);
    }

    static void Harvest(AutomationElement node, List<string> lines, int depth)
    {
        if (lines.Count >= 60 || depth > 5)
            return;
        var name = node.Current.Name ?? "";
        var type = node.Current.ControlType;
        if (name.Length > 0 && name.Length < 500
            && name.IndexOf("terminal input", StringComparison.OrdinalIgnoreCase) < 0
            && name.IndexOf("终端输入", StringComparison.Ordinal) < 0
            && (type == ControlType.Text || type == ControlType.Edit || type == ControlType.Document))
        {
            lines.Add(name);
        }

        var child = TreeWalker.ControlViewWalker.GetFirstChild(node);
        while (child != null && lines.Count < 60)
        {
            Harvest(child, lines, depth + 1);
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
        }
    }

    static void TrimStatus(List<string> lines)
    {
        while (lines.Count > 1)
        {
            var last = Normalize(lines[^1]);
            if (last.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            var earlierHasInput = false;
            for (var i = 0; i < lines.Count - 1; i++)
            {
                var row = Normalize(lines[i]);
                if (HasCjk(row) || StartsInputRow(row))
                {
                    earlierHasInput = true;
                    break;
                }
            }

            if (earlierHasInput && !HasCjk(last) && !StartsInputRow(last) && last.Length < 40)
            {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            break;
        }
    }

    static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        var line = raw.Replace("\r", "").Replace("\n", "").Trim();
        if (line.Length == 0)
            return "";
        line = BoxEdge.Replace(line, "").Trim();
        return line;
    }

    static bool StartsInputRow(string normalized)
    {
        if (normalized.Length == 0)
            return false;
        return ShellPrompt.IsMatch(normalized) || TuiPrompt.IsMatch(normalized) || FancyPrompt.IsMatch(normalized);
    }

    static string StripStatusTail(string line)
    {
        var tail = StatusTail.Match(line);
        return tail.Success ? line[..tail.Index].TrimEnd() : line;
    }

    static string StripGutter(string line)
    {
        var gutter = LineGutter.Match(line);
        if (!gutter.Success)
            return line;
        if (!int.TryParse(gutter.Groups["n"].Value, out var number) || number is < 1 or > 99)
            return line;
        var rest = line[gutter.Length..].Trim();
        if (rest.Length == 0 || !StartsWithText(rest))
            return line;
        return rest;
    }

    static bool StartsWithText(string text)
    {
        var ch = text[0];
        return ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '\u4e00' and <= '\u9fff');
    }

    static bool IsChrome(string line)
    {
        if (line.Length == 0)
            return false;
        if (line.Contains("[stop]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("always-approve", StringComparison.OrdinalIgnoreCase))
            return true;
        if (line.Contains('·') && !HasCjk(line) && line.Length < 60)
            return true;
        if (!HasCjk(line)
            && (line.StartsWith("Grok ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Claude", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    static string StripPrompt(string line)
    {
        var shell = ShellPrompt.Match(line);
        if (shell.Success)
            return line[shell.Length..].Trim();
        var tui = TuiPrompt.Match(line);
        if (tui.Success)
            return line[tui.Length..].Trim();
        var fancy = FancyPrompt.Match(line);
        if (fancy.Success)
            return line[fancy.Length..].Trim();
        return line.Trim();
    }

    static bool HasCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF)
                return true;
        }

        return false;
    }

    static Rectangle ElementBounds(AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0 || double.IsInfinity(rect.Width))
                return Rectangle.Empty;
            return Rectangle.Round(new RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height));
        }
        catch
        {
            return Rectangle.Empty;
        }
    }

    static bool IsAutomationFault(Exception ex)
    {
        return ex is InvalidOperationException or ElementNotAvailableException or System.Runtime.InteropServices.COMException;
    }
}
