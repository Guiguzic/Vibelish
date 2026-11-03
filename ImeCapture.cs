using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Vibelish;

sealed class ImeCapture : IDisposable
{
    const int WhGetMessage = 3;
    const int WhCallWndProc = 4;
    const int ActiveOffset = 4;
    const int ResetOffset = 8;
    const int SeqOffset = 16;
    const int CommittedLenOffset = 20;
    const int CompLenOffset = 24;
    const int CommittedOffset = 28;
    const int CompOffset = 4028;
    const int NoteOffset = 4828;
    const int ComposingOffset = 4832;

    readonly string _dllPath;
    IntPtr _module;
    IntPtr _getMsgHook;
    IntPtr _callWndHook;
    Mutex? _mutex;
    MemoryMappedFile? _mapping;
    MemoryMappedViewAccessor? _view;

    public static string DllPath()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "hook", "imehook.dll");
        if (!File.Exists(source))
            source = Path.Combine(AppContext.BaseDirectory, "imehook.dll");
        if (!File.Exists(source))
            return source;

        // Load a hash-named copy from LocalAppData. The hook DLL is injected into every
        // process that is hooked, which keeps it locked there; loading a copy means the
        // shipped file can be replaced when the app is updated.
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vibelish",
                "hook");
            Directory.CreateDirectory(directory);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))[..16];
            var cached = Path.Combine(directory, "imehook-" + hash + ".dll");
            if (!File.Exists(cached))
                File.Copy(source, cached, true);
            return cached;
        }
        catch
        {
            return source;
        }
    }

    public ImeCapture(string dllPath)
    {
        _dllPath = dllPath;
    }

    public bool Installed { get; private set; }
    public string Error { get; private set; } = "";

    public void Install()
    {
        _mutex = new Mutex(false, @"Local\Vibelish.Ime.Lock");
        _mapping = MemoryMappedFile.CreateOrOpen(@"Local\Vibelish.Ime.v1", 8192);
        _view = _mapping.CreateViewAccessor();
        _module = LoadLibrary(_dllPath);
        if (_module == IntPtr.Zero)
        {
            Error = "无法加载输入法钩子：" + _dllPath;
            return;
        }

        var getMsg = GetProcAddress(_module, "GetMsgProc");
        var callWnd = GetProcAddress(_module, "CallWndProc");
        if (getMsg == IntPtr.Zero || callWnd == IntPtr.Zero)
        {
            Error = "输入法钩子缺少导出函数";
            return;
        }

        _getMsgHook = SetWindowsHookEx(WhGetMessage, getMsg, _module, 0);
        _callWndHook = SetWindowsHookEx(WhCallWndProc, callWnd, _module, 0);
        Installed = _getMsgHook != IntPtr.Zero && _callWndHook != IntPtr.Zero;
        if (!Installed)
            Error = "安装输入法钩子失败，错误码 " + Marshal.GetLastWin32Error();
    }

    public void BeginSession()
    {
        WithLock(() =>
        {
            var reset = _view!.ReadInt32(ResetOffset) + 1;
            _view.Write(ResetOffset, reset);
            _view.Write(CommittedLenOffset, 0);
            _view.Write(CompLenOffset, 0);
            _view.Write(NoteOffset, 0);
            _view.Write(ComposingOffset, 0);
            _view.Write(ActiveOffset, 1);
        });
    }

    public void EndSession()
    {
        WithLock(() => _view!.Write(ActiveOffset, 0));
    }

    public (string Committed, string Composition, int Seq, int Notes, bool Composing) Read()
    {
        string committed = "";
        string composition = "";
        var seq = 0;
        var notes = 0;
        var composing = false;
        WithLock(() =>
        {
            seq = _view!.ReadInt32(SeqOffset);
            notes = _view.ReadInt32(NoteOffset);
            composing = _view.ReadInt32(ComposingOffset) != 0;
            committed = ReadText(CommittedLenOffset, CommittedOffset, 1999);
            composition = ReadText(CompLenOffset, CompOffset, 399);
        });
        return (committed, composition, seq, notes, composing);
    }

    public void Dispose()
    {
        EndSession();
        if (_getMsgHook != IntPtr.Zero) UnhookWindowsHookEx(_getMsgHook);
        if (_callWndHook != IntPtr.Zero) UnhookWindowsHookEx(_callWndHook);
        _getMsgHook = IntPtr.Zero;
        _callWndHook = IntPtr.Zero;
        _view?.Dispose();
        _mapping?.Dispose();
        _mutex?.Dispose();
        if (_module != IntPtr.Zero) FreeLibrary(_module);
        _module = IntPtr.Zero;
    }

    string ReadText(int lengthOffset, int textOffset, int max)
    {
        var length = _view!.ReadInt32(lengthOffset);
        if (length <= 0) return "";
        if (length > max) length = max;
        var buffer = new char[length];
        _view.ReadArray(textOffset, buffer, 0, length);
        return new string(buffer);
    }

    void WithLock(Action action)
    {
        if (_view == null || _mutex == null) return;
        try
        {
            if (!_mutex.WaitOne(200)) return;
        }
        catch (AbandonedMutexException)
        {
            // 被注入的程序拿着锁退出了。锁已经归我们，照常用完再释放；
            // 否则异常一抛，锁永远不还，钩子那边从此写不进共享内存。
        }

        try { action(); }
        finally { _mutex.ReleaseMutex(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, IntPtr proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
}
