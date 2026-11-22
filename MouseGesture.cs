using System.Runtime.InteropServices;

namespace Vibelish;

/// <summary>
/// 按住鼠标右键拖一下：拖过的地方截图识别、翻译。
/// 右键按下先拦住；拖动超过几个像素就是手势，松开时不弹右键菜单；没拖动就把右键单击补回去。
/// 鼠标钩子里只记坐标，别的活都排到 UI 线程去做（钩子回调太慢 Windows 会把它卸掉）。
/// </summary>
sealed class MouseGesture : IDisposable
{
    const int WhMouseLl = 14;
    const int WmMouseMove = 0x0200;
    const int WmRButtonDown = 0x0204;
    const int WmRButtonUp = 0x0205;
    // 右键单击时手抖一下不能算拖动（终端里右键是粘贴，误判就把粘贴吞了）：要拖得够远、按得够久。
    const int Threshold = 28;
    const int MinHoldMs = 180;

    readonly HookProc _proc;
    readonly Action<Action> _dispatch;
    IntPtr _hook;
    bool _pressed;
    bool _dragging;
    Point _start;
    long _pressedAt;
    Point _last;
    long _lastMoveSent;

    public MouseGesture(Action<Action> dispatch)
    {
        _proc = Hook;
        _dispatch = dispatch;
    }

    /// <summary>拖动中，参数是当前框（屏幕物理像素），用来画选框。</summary>
    public event Action<Rectangle>? Dragging;

    /// <summary>松开右键，参数是拖过的框；拖动取消时不触发。</summary>
    public event Action<Rectangle>? Finished;

    public bool Installed => _hook != IntPtr.Zero;

    Thread? _thread;
    uint _threadId;

    /// <summary>
    /// 钩子装在一个专门的线程上，自己跑消息循环：全系统的鼠标移动都要经过钩子，
    /// 放在 UI 线程上，界面一卡（读输入框、识别文字）整个鼠标就跟着卡。
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled && _thread == null)
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _hook = SetWindowsHookEx(WhMouseLl, _proc, IntPtr.Zero, 0);
                if (_hook == IntPtr.Zero)
                    TraceLog.Note("mouse hook failed " + Marshal.GetLastWin32Error());
                ready.Set();
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                if (_hook != IntPtr.Zero)
                    UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            })
            {
                IsBackground = true,
                Name = "Vibelish.MouseGesture",
            };
            _thread.Start();
            ready.Wait(2000);
        }
        else if (!enabled && _thread != null)
        {
            PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
            _thread.Join(1000);
            _thread = null;
            _pressed = false;
            _dragging = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [DllImport("user32.dll")]
    static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    /// <summary>起点和终点围成的框；几乎横着划的一道线，就上下各放大半行，框住那一行字。</summary>
    public static Rectangle AreaOf(Point a, Point b, int lineHeight)
    {
        var left = Math.Min(a.X, b.X);
        var right = Math.Max(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var bottom = Math.Max(a.Y, b.Y);
        if (bottom - top < lineHeight)
        {
            var middle = (top + bottom) / 2;
            top = middle - lineHeight / 2;
            bottom = middle + lineHeight / 2;
        }

        return Rectangle.FromLTRB(left, top, Math.Max(right, left + 8), bottom);
    }

    static int LineHeight(Point at)
    {
        try
        {
            var screen = Screen.FromPoint(at);
            var dpi = 96u;
            var monitor = MonitorFromPoint(at, 2);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var x, out _) == 0)
                dpi = x;
            _ = screen;
            return (int)Math.Round(34 * dpi / 96.0);
        }
        catch
        {
            return 34;
        }
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<MsLl>(lParam);
                var injected = (data.Flags & 0x01) != 0;
                var message = (int)wParam;
                var point = new Point(data.X, data.Y);
                if (!injected)
                {
                    switch (message)
                    {
                        case WmRButtonDown:
                            // 本来就用右键拖动的程序（拖文件、平移画布）不拦。
                            if (OwnsRightDrag(point))
                                break;
                            _pressed = true;
                            _dragging = false;
                            _pressedAt = Environment.TickCount64;
                            _start = point;
                            _last = point;
                            return (IntPtr)1;
                        case WmMouseMove when _pressed:
                            _last = point;
                            if (!_dragging && Environment.TickCount64 - _pressedAt >= MinHoldMs
                                && (Math.Abs(point.X - _start.X) > Threshold || Math.Abs(point.Y - _start.Y) > Threshold))
                                _dragging = true;
                            if (_dragging && Environment.TickCount64 - _lastMoveSent > 15)
                            {
                                _lastMoveSent = Environment.TickCount64;
                                var area = AreaOf(_start, point, LineHeight(_start));
                                _dispatch(() => Dragging?.Invoke(area));
                            }

                            break;
                        case WmRButtonUp when _pressed:
                            _pressed = false;
                            if (_dragging)
                            {
                                _dragging = false;
                                var area = AreaOf(_start, point, LineHeight(_start));
                                _dispatch(() => Finished?.Invoke(area));
                            }
                            else
                            {
                                // 没拖动：普通右键，补发一次（带注入标记，不会再被这里拦）。
                                _dispatch(KeyboardWriter.RightClick);
                            }

                            return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // 鼠标钩子里抛异常会让鼠标卡顿，吞掉。
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => SetEnabled(false);

    static readonly string[] RightDragApps =
    [
        "explorer", "kicad", "pcbnew", "eeschema", "gerbview", "pl_editor", "blender",
        "freecad", "inkscape", "gimp", "mstsc",
    ];

    readonly Dictionary<uint, bool> _ownsCache = new();

    bool OwnsRightDrag(Point point)
    {
        var hwnd = WindowFromPoint(point);
        if (hwnd == IntPtr.Zero)
            return false;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return false;
        if (_ownsCache.TryGetValue(pid, out var owns))
            return owns;
        owns = false;
        var process = OpenProcess(0x1000, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                var size = buffer.Capacity;
                if (QueryFullProcessImageName(process, 0, buffer, ref size))
                {
                    var name = Path.GetFileNameWithoutExtension(buffer.ToString());
                    owns = RightDragApps.Any(app => name.Equals(app, StringComparison.OrdinalIgnoreCase));
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        if (_ownsCache.Count > 200)
            _ownsCache.Clear();
        _ownsCache[pid] = owns;
        return owns;
    }

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MsLl
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}

/// <summary>右键拖动时的选框：半透明、点不到、不抢焦点。</summary>
sealed class DragFrame : Form
{
    public DragFrame()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Current.Accent;
        Opacity = 0.22;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // 分层 + 鼠标穿透 + 不激活 + 不进 Alt+Tab。
            cp.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
            return cp;
        }
    }

    public void ShowArea(Rectangle area)
    {
        Bounds = area;
        if (!Visible)
            Show();
    }
}
