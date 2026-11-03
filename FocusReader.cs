using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace InputTranslate;

sealed class FocusSnapshot
{
    public bool Ok { get; init; }
    public string Reason { get; init; } = "";
    public string App { get; init; } = "";
    public string Window { get; init; } = "";
    public string Control { get; init; } = "";
    public string Paragraph { get; init; } = "";
    public bool WholeField { get; init; }
    public bool CanWrite { get; init; }
    public bool NeedsOcr { get; init; }
    public Rectangle Bounds { get; init; }
    public int Key { get; init; }
    public AutomationElement? Element { get; init; }

    public static FocusSnapshot Fail(string reason) => new() { Ok = false, Reason = reason };

    public FocusSnapshot WithParagraph(string paragraph)
    {
        return new FocusSnapshot
        {
            Ok = Ok,
            Reason = Reason,
            App = App,
            Window = Window,
            Control = Control,
            Paragraph = paragraph,
            WholeField = WholeField,
            CanWrite = CanWrite,
            NeedsOcr = NeedsOcr,
            Bounds = Bounds,
            Key = Key,
            Element = Element,
        };
    }
}

static class FocusProbe
{
    public static void Dump(string path)
    {
        var lines = new System.Collections.Generic.List<string>();
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null)
            {
                lines.Add("NO_FOCUS");
            }
            else
            {
                DumpOne(lines, "focus", focused);
                DumpSearch(lines);
                var walker = TreeWalker.ControlViewWalker;
                var child = walker.GetFirstChild(focused);
                var count = 0;
                while (child != null && count < 8)
                {
                    DumpOne(lines, "child" + count, child);
                    child = walker.GetNextSibling(child);
                    count++;
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add("ERR " + ex.GetType().Name + " " + ex.Message);
        }

        File.WriteAllLines(path, lines);
    }

    static void DumpOne(System.Collections.Generic.List<string> lines, string label, AutomationElement element)
    {
        var cls = "";
        var control = "";
        var nameLength = 0;
        var valueLength = 0;
        var cjk = 0;
        var hwnd = 0;
        try
        {
            cls = element.Current.ClassName ?? "";
            control = element.Current.ControlType?.ProgrammaticName ?? "";
            nameLength = (element.Current.Name ?? "").Length;
            hwnd = element.Current.NativeWindowHandle;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                var value = ((ValuePattern)pattern).Current.Value ?? "";
                valueLength = value.Length;
                foreach (var ch in value)
                {
                    if (ch >= 0x4E00 && ch <= 0x9FFF)
                        cjk++;
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add(label + " ERR " + ex.GetType().Name);
            return;
        }

        lines.Add(label + " cls=" + cls + " ct=" + control + " nameLen=" + nameLength + " valLen=" + valueLength + " cjk=" + cjk + " hwnd=" + hwnd);
    }

    static void DumpSearch(System.Collections.Generic.List<string> lines)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("SearchHost"))
        {
            try
            {
                var root = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                if (root == null)
                {
                    lines.Add("search pid=" + process.Id + " no-root");
                    continue;
                }

                var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                lines.Add("search pid=" + process.Id + " edits=" + edits.Count);
                var shown = 0;
                foreach (AutomationElement edit in edits)
                {
                    if (shown++ >= 4)
                        break;
                    DumpOne(lines, "search-edit", edit);
                }
            }
            catch (Exception ex)
            {
                lines.Add("search ERR " + ex.GetType().Name);
            }
        }
    }
}

static class FocusReader
{
    const int MaxChars = 1500;

    public static FocusSnapshot Read(int selfPid)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null)
                return FocusSnapshot.Fail("没有焦点");

            AutomationElement? node = focused;
            for (var depth = 0; depth < 12 && node != null; depth++)
            {
                if (IsPassword(node))
                    return FocusSnapshot.Fail("密码框已跳过");

                var pid = ProcessId(node);
                if (pid == selfPid)
                    return FocusSnapshot.Fail("自身窗口");

                // 终端没有编辑框。必须先于 TryReadText：控制台的“段落”等于整屏缓冲。
                if (TerminalReader.TryRead(node, out var terminalText, out var terminalBounds))
                    return Success(node, pid, terminalText, whole: true, canWrite: false, needsOcr: false, terminalBounds);

                if (TryReadText(node, out var paragraph, out var whole, out var canWrite))
                    return Success(node, pid, paragraph, whole, canWrite, needsOcr: false);

                if (IsCustomInput(node, out var ocrBounds))
                    return Success(node, pid, "", whole: false, canWrite: false, needsOcr: true, ocrBounds);

                node = TreeWalker.ControlViewWalker.GetParent(node);
            }

            return FocusSnapshot.Fail("当前焦点不是输入框");
        }
        catch (ElementNotAvailableException)
        {
            return FocusSnapshot.Fail("焦点已变化");
        }
        catch (Exception ex)
        {
            var detail = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null)
                detail += " / inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            TraceLog.Note("focus " + TraceLog.Safe(detail));
            return FocusSnapshot.Fail(ex.Message);
        }
    }

    public static bool TryWrite(FocusSnapshot? snapshot, string english, out string message)
    {
        message = "";
        if (snapshot == null || !snapshot.Ok || snapshot.Element == null)
        {
            message = "没有可写入的输入框";
            return false;
        }

        if (!snapshot.CanWrite)
        {
            message = "这个输入框不能直接写入，已复制英文，请按 Ctrl+V";
            return false;
        }

        if (!snapshot.WholeField)
        {
            message = "这段话不是输入框的全部内容，已复制英文，请在输入框按 Ctrl+V";
            return false;
        }

        try
        {
            if (!snapshot.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                message = "这个输入框不支持直接写入，已复制英文";
                return false;
            }

            var value = (ValuePattern)pattern;
            if (value.Current.IsReadOnly)
            {
                message = "这个输入框是只读的，已复制英文";
                return false;
            }

            value.SetValue(english);
            message = "已写入输入框";
            return true;
        }
        catch (Exception ex)
        {
            message = "不能直接写入（" + ex.Message + "），已复制英文";
            return false;
        }
    }

    static FocusSnapshot Success(AutomationElement node, int pid, string paragraph, bool whole, bool canWrite, bool needsOcr, Rectangle? bounds = null)
    {
        var window = RootWindow(node);
        var app = ProcessName(pid);
        var title = WindowTitle(window);
        var control = ControlLabel(node);
        return new FocusSnapshot
        {
            Ok = true,
            App = app,
            Window = title,
            Control = control,
            Paragraph = paragraph,
            WholeField = whole,
            CanWrite = canWrite,
            NeedsOcr = needsOcr,
            Bounds = bounds ?? ToRectangle(node.Current.BoundingRectangle),
            Key = HashCode.Combine(pid, title, control),
            Element = node,
        };
    }

    static bool IsCustomInput(AutomationElement element, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        try
        {
            var cls = element.Current.ClassName ?? "";
            var id = element.Current.AutomationId ?? "";
            var custom = cls.Contains("mmui::", StringComparison.Ordinal)
                || id.Contains("input", StringComparison.OrdinalIgnoreCase);
            if (!custom)
                return false;
            bounds = ToRectangle(element.Current.BoundingRectangle);
            return OcrReader.CanCapture(bounds);
        }
        catch
        {
            return false;
        }
    }

    static bool TryReadText(AutomationElement element, out string paragraph, out bool whole, out bool canWrite)
    {
        paragraph = "";
        whole = false;
        canWrite = false;

        string? fromSelection = null;
        if (AllowsTextPattern(element) && element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject))
        {
            try
            {
                var textPattern = (TextPattern)textPatternObject;
                var ranges = textPattern.GetSelection();
                if (ranges.Length > 0)
                {
                    var range = ranges[0].Clone();
                    range.ExpandToEnclosingUnit(TextUnit.Paragraph);
                    var raw = range.GetText(MaxChars) ?? "";
                    if (!string.IsNullOrWhiteSpace(raw))
                        fromSelection = Clean(raw);
                }
            }
            catch
            {
                fromSelection = null;
            }
        }

        string? value = null;
        var hasValue = false;
        var readOnly = true;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject))
        {
            try
            {
                var valuePattern = (ValuePattern)valuePatternObject;
                hasValue = true;
                readOnly = valuePattern.Current.IsReadOnly;
                value = valuePattern.Current.Value;
            }
            catch
            {
                hasValue = false;
            }
        }

        if (fromSelection != null && (value == null || fromSelection.Length + 8 < value.Trim().Length || value.Trim().Length > 800))
        {
            paragraph = Limit(fromSelection);
            whole = value != null && string.Equals(value.Trim(), paragraph, StringComparison.Ordinal);
            canWrite = whole && hasValue && !readOnly;
            return paragraph.Length > 0;
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            paragraph = PickFromValue(value, out whole);
            canWrite = whole && hasValue && !readOnly;
            return paragraph.Length > 0;
        }

        if (fromSelection != null)
        {
            paragraph = Limit(fromSelection);
            whole = false;
            canWrite = false;
            return paragraph.Length > 0;
        }

        return false;
    }

    static bool AllowsTextPattern(AutomationElement element)
    {
        try
        {
            var cls = element.Current.ClassName ?? "";
            if (cls.Contains("mmui", StringComparison.OrdinalIgnoreCase))
                return false;
            var control = element.Current.ControlType;
            if (control == ControlType.Document || control == ControlType.Edit || control == ControlType.ComboBox)
                return true;
            return cls.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("TextBox", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static string PickFromValue(string value, out bool whole)
    {
        var normalized = value.Replace("\r\n", "\n").Trim();
        if (normalized.Length > MaxChars)
        {
            whole = false;
            var lastBreak = normalized.LastIndexOf('\n');
            var tail = lastBreak >= 0 ? normalized[(lastBreak + 1)..].Trim() : normalized[^Math.Min(500, normalized.Length)..];
            return Limit(tail);
        }

        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (blocks.Length <= 1)
        {
            whole = true;
            return normalized;
        }

        whole = false;
        return blocks[^1];
    }

    static string Clean(string text)
    {
        return text.Replace("\r\n", "\n").Trim();
    }

    static string Limit(string text)
    {
        text = text.Trim();
        if (text.Length <= MaxChars)
            return text;
        return text[^MaxChars..].Trim();
    }

    static bool IsPassword(AutomationElement element)
    {
        try
        {
            if (element.Current.IsPassword)
                return true;
            var name = element.Current.Name ?? "";
            if (name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("密码", StringComparison.Ordinal))
            {
                var control = element.Current.ControlType;
                return control == ControlType.Edit || control == ControlType.Document || control == ControlType.Custom;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    static string ControlLabel(AutomationElement element)
    {
        try
        {
            var name = element.Current.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name.Trim();
            return element.Current.LocalizedControlType ?? "输入框";
        }
        catch
        {
            return "输入框";
        }
    }

    static AutomationElement RootWindow(AutomationElement element)
    {
        var current = element;
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            while (true)
            {
                var parent = walker.GetParent(current);
                if (parent == null)
                    break;
                current = parent;
            }
        }
        catch
        {
            return element;
        }

        return current;
    }

    static int ProcessId(AutomationElement element)
    {
        try
        {
            var hwnd = Native.FindHwnd(element);
            if (hwnd != IntPtr.Zero)
            {
                _ = Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != 0)
                    return (int)pid;
            }

            return element.Current.ProcessId;
        }
        catch
        {
            return 0;
        }
    }

    static string ProcessName(int pid)
    {
        if (pid <= 0)
            return "未知程序";
        try
        {
            return System.Diagnostics.Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return "未知程序";
        }
    }

    static string WindowTitle(AutomationElement window)
    {
        try
        {
            var hwnd = Native.FindHwnd(window);
            if (hwnd != IntPtr.Zero)
            {
                var root = Native.GetAncestor(hwnd, Native.GaRoot);
                if (root == IntPtr.Zero)
                    root = hwnd;
                var sb = new StringBuilder(512);
                _ = Native.GetWindowText(root, sb, sb.Capacity);
                if (sb.Length > 0)
                    return sb.ToString();
            }

            return window.Current.Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    static Rectangle ToRectangle(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.Width) || rect.Width <= 0 || rect.Height <= 0)
            return Rectangle.Empty;
        return Rectangle.Round(new RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height));
    }
}

static class Native
{
    public const uint GaRoot = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder className, int count);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    public static IntPtr FindHwnd(AutomationElement element)
    {
        try
        {
            var handle = element.Current.NativeWindowHandle;
            if (handle != 0)
                return new IntPtr(handle);
        }
        catch
        {
            return IntPtr.Zero;
        }

        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var node = element;
            for (var i = 0; i < 8 && node != null; i++)
            {
                var handle = node.Current.NativeWindowHandle;
                if (handle != 0)
                    return new IntPtr(handle);
                node = walker.GetParent(node);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }
}
