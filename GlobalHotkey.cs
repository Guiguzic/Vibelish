using System.Runtime.InteropServices;

namespace InputTranslate;

/// <summary>一个热键组合，例如 Ctrl+Alt+T。</summary>
readonly record struct HotkeySpec(bool Ctrl, bool Alt, bool Shift, bool Win, Keys Key)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public uint Modifiers => (Ctrl ? ModControl : 0) | (Alt ? ModAlt : 0) | (Shift ? ModShift : 0) | (Win ? ModWin : 0);

    /// <summary>必须带 Ctrl、Alt 或 Win，否则会吞掉正常打字。</summary>
    public bool IsValid => Key != Keys.None && (Ctrl || Alt || Win) && !IsModifierKey(Key);

    public static HotkeySpec FromKeys(Keys modifiers, Keys key) => new(
        (modifiers & Keys.Control) != 0,
        (modifiers & Keys.Alt) != 0,
        (modifiers & Keys.Shift) != 0,
        false,
        key & Keys.KeyCode);

    public static HotkeySpec Parse(string text) =>
        TryParse(text, out var spec) ? spec : throw new FormatException(text);

    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        bool ctrl = false, alt = false, shift = false, win = false;
        var key = Keys.None;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                return false;
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    continue;
                case "alt":
                    alt = true;
                    continue;
                case "shift":
                    shift = true;
                    continue;
                case "win":
                    win = true;
                    continue;
            }

            if (key != Keys.None)
                return false;
            key = ParseKey(part);
            if (key == Keys.None)
                return false;
        }

        spec = new HotkeySpec(ctrl, alt, shift, win, key);
        return spec.IsValid;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + (key - Keys.NumPad0),
        Keys.Oemtilde => "`",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.OemPipe => "\\",
        Keys.Return => "Enter",
        Keys.Next => "PageDown",
        Keys.Prior => "PageUp",
        _ => key.ToString(),
    };

    static Keys ParseKey(string name)
    {
        if (name.Length == 1 && char.IsDigit(name[0]))
            return Keys.D0 + (name[0] - '0');
        if (name.Length == 1 && char.IsLetter(name[0]))
            return Keys.A + (char.ToUpperInvariant(name[0]) - 'A');
        foreach (Keys candidate in Enum.GetValues(typeof(Keys)))
        {
            if (KeyName(candidate).Equals(name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return Enum.TryParse<Keys>(name, true, out var parsed) && !IsModifierKey(parsed) ? parsed & Keys.KeyCode : Keys.None;
    }

    public static bool IsModifierKey(Keys key) =>
        (key & Keys.KeyCode) is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin or Keys.None;
}

/// <summary>系统热键（注册在一个只收消息的窗口上，不会在模态对话框里丢失），
/// 外加一个只在听写期间安装的键盘钩子：回车结束、Esc 取消。</summary>
sealed class GlobalHotkey : IDisposable
{
    const int WhKeyboardLl = 13;
    const int WmKeyDown = 0x0100;
    const int WmSysKeyDown = 0x0104;
    const int VkReturn = 0x0D;
    const int VkEscape = 0x1B;
    const int VkShift = 0x10;
    const int VkControl = 0x11;
    const int VkMenu = 0x12;
    const int HotkeyId = 1;
    const uint ModNoRepeat = 0x4000;

    static readonly string[] Fallbacks = ["Ctrl+Alt+T", "Ctrl+Alt+E", "Ctrl+Alt+Q", "Ctrl+Shift+F9", "Ctrl+Alt+G"];

    readonly HookProc _proc;
    readonly Action<Action> _dispatch;
    HotkeyWindow? _window;
    IntPtr _hook;
    string _preferred = AppConfig.DefaultHotkey;

    /// <param name="dispatch">把回调排到 UI 线程上异步执行。键盘钩子里不能直接做耗时的界面工作，
    /// 超时后 Windows 会悄悄卸掉钩子。</param>
    public GlobalHotkey(Action<Action> dispatch)
    {
        _proc = Hook;
        _dispatch = dispatch;
    }

    public bool HotkeyRegistered { get; private set; }
    public bool HookInstalled => _hook != IntPtr.Zero;
    public string HotkeyText { get; private set; } = "";
    public string Preferred => _preferred;

    /// <summary>首选组合被占用，换成了备用组合。</summary>
    public bool UsingFallback => HotkeyRegistered && !string.Equals(HotkeyText, _preferred, StringComparison.OrdinalIgnoreCase);

    public string Error { get; private set; } = "";
    public bool Composing { get; set; }

    public event Action? ToggleRequested;

    /// <summary>参数为 true 表示 Esc（取消），false 表示回车（完成）。</summary>
    public event Action<bool>? EndRequested;

    /// <summary>注册热键：先试首选组合，被占用时依次试备用组合。</summary>
    public void Register(string preferred)
    {
        Unregister();
        _preferred = HotkeySpec.TryParse(preferred, out var spec) ? spec.ToString() : AppConfig.DefaultHotkey;
        _window ??= new HotkeyWindow(() => ToggleRequested?.Invoke());
        Error = "";
        var tried = new List<string> { _preferred };
        foreach (var fallback in Fallbacks)
        {
            if (!tried.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                tried.Add(fallback);
        }

        var lastError = 0;
        foreach (var text in tried)
        {
            var candidate = HotkeySpec.Parse(text);
            if (RegisterHotKey(_window.Handle, HotkeyId, candidate.Modifiers | ModNoRepeat, (uint)candidate.Key))
            {
                HotkeyRegistered = true;
                HotkeyText = candidate.ToString();
                return;
            }

            lastError = Marshal.GetLastWin32Error();
        }

        HotkeyText = "";
        Error = "热键都被其他程序占用了（错误码 " + lastError + "）";
    }

    public void Unregister()
    {
        if (!HotkeyRegistered || _window == null)
            return;
        UnregisterHotKey(_window.Handle, HotkeyId);
        HotkeyRegistered = false;
    }

    /// <summary>开始听写时装上键盘钩子，结束时卸掉，平时不经过本程序。</summary>
    public void BeginCapture()
    {
        Composing = false;
        if (_hook != IntPtr.Zero)
            return;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            TraceLog.Note("keyboard hook failed " + Marshal.GetLastWin32Error());
    }

    public void EndCapture()
    {
        Composing = false;
        if (_hook == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose()
    {
        EndCapture();
        Unregister();
        _window?.DestroyHandle();
        _window = null;
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && !Composing)
            {
                var message = (int)wParam;
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    var key = Marshal.PtrToStructure<KbdLl>(lParam);
                    var injected = (key.Flags & 0x10) != 0;
                    // Shift+回车、Ctrl+回车在聊天软件里是换行，放过去。
                    var modified = Down(VkShift) || Down(VkControl) || Down(VkMenu);
                    if (!injected && !modified && key.VkCode is VkReturn or VkEscape)
                    {
                        var cancel = key.VkCode == VkEscape;
                        _dispatch(() => EndRequested?.Invoke(cancel));
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // A throw inside a low-level hook can stall keyboard input.
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    sealed class HotkeyWindow : NativeWindow
    {
        const int WmHotkey = 0x0312;
        static readonly IntPtr MessageOnly = new(-3);
        readonly Action _onHotkey;

        public HotkeyWindow(Action onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams { Caption = "InputTranslate.Hotkey", Parent = MessageOnly });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
            {
                _onHotkey();
                return;
            }

            base.WndProc(ref m);
        }
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KbdLl
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);
}
