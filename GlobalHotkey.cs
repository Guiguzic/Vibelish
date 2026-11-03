using System.Runtime.InteropServices;

namespace InputTranslate;

/// <summary>A registered system hotkey (no keyboard hook needed) plus a small hook that
/// swallows Enter/Esc only while a capture session is running.</summary>
sealed class GlobalHotkey : IMessageFilter, IDisposable
{
    const int WhKeyboardLl = 13;
    const int WmKeyDown = 0x0100;
    const int WmSysKeyDown = 0x0104;
    const int WmHotkey = 0x0312;
    const uint ModAlt = 0x0001;
    const uint ModControl = 0x0002;
    const uint ModShift = 0x0004;
    const uint ModNoRepeat = 0x4000;
    const int VkReturn = 0x0D;
    const int VkEscape = 0x1B;

    static readonly (uint Mods, uint Vk, string Text)[] Candidates =
    [
        (ModControl | ModAlt, (uint)'T', "Ctrl+Alt+T"),
        (ModControl | ModAlt, (uint)'E', "Ctrl+Alt+E"),
        (ModControl | ModAlt, (uint)'Q', "Ctrl+Alt+Q"),
        (ModControl | ModShift, 0x78, "Ctrl+Shift+F9"),
        (ModControl | ModAlt, (uint)'G', "Ctrl+Alt+G"),
    ];

    readonly HookProc _proc;
    IntPtr _hook;
    bool _filterAdded;

    public GlobalHotkey()
    {
        _proc = Hook;
    }

    public bool HotkeyRegistered { get; private set; }
    public bool HookInstalled => _hook != IntPtr.Zero;
    public string HotkeyText { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool Capturing { get; set; }
    public bool Composing { get; set; }

    public event Action? ToggleRequested;
    public event Action? EndRequested;

    public void Install()
    {
        Application.AddMessageFilter(this);
        _filterAdded = true;
        SetEnabled(true);

        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero && Error.Length == 0)
            Error = "键盘钩子安装失败，错误码 " + Marshal.GetLastWin32Error();
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            Unregister();
            return;
        }

        if (HotkeyRegistered)
            return;

        Error = "";
        foreach (var candidate in Candidates)
        {
            if (!RegisterHotKey(IntPtr.Zero, 1, candidate.Mods | ModNoRepeat, candidate.Vk))
                continue;
            HotkeyRegistered = true;
            HotkeyText = candidate.Text;
            break;
        }

        if (!HotkeyRegistered)
            Error = "系统热键注册失败，错误码 " + Marshal.GetLastWin32Error();
    }

    public void Unregister()
    {
        if (!HotkeyRegistered)
            return;
        UnregisterHotKey(IntPtr.Zero, 1);
        HotkeyRegistered = false;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmHotkey)
            return false;
        if (m.WParam.ToInt32() != 1)
            return false;
        ToggleRequested?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (_filterAdded)
        {
            Application.RemoveMessageFilter(this);
            _filterAdded = false;
        }

        Unregister();
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && Capturing && !Composing)
            {
                var message = (int)wParam;
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    var key = Marshal.PtrToStructure<KbdLl>(lParam);
                    if (key.VkCode is VkReturn or VkEscape)
                    {
                        EndRequested?.Invoke();
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
}
