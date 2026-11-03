using System.Runtime.InteropServices;
using System.Text;

namespace InputTranslate;

/// <summary>
/// 从目标控制台的屏幕缓冲读取光标所在行。Windows Terminal 和经典控制台的
/// UI Automation 文本范围会触发访问冲突，不能用来取正文。
/// 每次读完立刻 FreeConsole：挂在别人的控制台上时，那个终端一关，
/// 系统会给所有挂着的进程发 CTRL_CLOSE_EVENT，托盘程序会被一起结束。
/// </summary>
static class ConsoleBuffer
{
    const uint GenericReadWrite = 0xC0000000;
    const uint FileShareReadWrite = 3;
    const uint OpenExisting = 3;

    static readonly object Gate = new();
    static uint _attachedPid;
    static IntPtr _output;

    public static bool TryReadRows(uint pid, out List<string> rows, out string title, out string error, out int cursorIndex)
    {
        rows = new List<string>();
        title = "";
        error = "";
        cursorIndex = 0;
        if (pid == 0)
        {
            error = "no-pid";
            return false;
        }

        lock (Gate)
        {
            try
            {
                return ReadAttached(pid, rows, out title, out error, out cursorIndex);
            }
            finally
            {
                Detach();
            }
        }
    }

    static bool ReadAttached(uint pid, List<string> rows, out string title, out string error, out int cursorIndex)
    {
        title = "";
        cursorIndex = 0;
        if (!EnsureAttached(pid, out error))
            return false;

        var handle = _output;
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            error = "no-output";
            return false;
        }

        if (!GetConsoleScreenBufferInfo(handle, out var info))
        {
            error = "buffer " + Marshal.GetLastWin32Error();
            return false;
        }

        var width = info.dwSize.X;
        if (width <= 0)
        {
            error = "width";
            return false;
        }

        if (width > 800)
            width = 800;

        var titleBuffer = new StringBuilder(512);
        _ = GetConsoleTitle(titleBuffer, (uint)titleBuffer.Capacity);
        title = titleBuffer.ToString();

        var y = info.dwCursorPosition.Y;
        var height = info.dwSize.Y;
        if (height <= 0)
            height = (short)(y + 1);
        // 光标经常停在多行输入的第一行，下面几行才是换行后的正文。
        var start = y > 4 ? y - 4 : 0;
        var end = y + 16;
        if (end >= height)
            end = height - 1;
        if (end < y)
            end = y;
        for (var row = start; row <= end; row++)
            rows.Add(ReadRow(handle, width, row));
        cursorIndex = y - start;
        return true;
    }

    public static void Release()
    {
        lock (Gate)
            Detach();
    }

    static void Detach()
    {
        CloseOutput();
        if (_attachedPid == 0)
            return;
        FreeConsole();
        _attachedPid = 0;
    }

    static bool EnsureAttached(uint pid, out string error)
    {
        error = "";
        if (_attachedPid == pid && _output != IntPtr.Zero && _output != new IntPtr(-1))
            return true;
        CloseOutput();
        if (_attachedPid != 0)
        {
            FreeConsole();
            _attachedPid = 0;
        }

        if (!AttachConsole(pid))
        {
            error = "attach " + Marshal.GetLastWin32Error();
            return false;
        }

        _attachedPid = pid;
        _output = CreateFile("CONOUT$", GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (_output == IntPtr.Zero || _output == new IntPtr(-1))
        {
            error = "conout " + Marshal.GetLastWin32Error();
            FreeConsole();
            _attachedPid = 0;
            _output = IntPtr.Zero;
            return false;
        }

        return true;
    }

    static void CloseOutput()
    {
        if (_output == IntPtr.Zero || _output == new IntPtr(-1))
        {
            _output = IntPtr.Zero;
            return;
        }

        CloseHandle(_output);
        _output = IntPtr.Zero;
    }

    static string ReadRow(IntPtr handle, int width, int y)
    {
        var buffer = new char[width];
        var coord = new Coord { X = 0, Y = (short)y };
        if (!ReadConsoleOutputCharacter(handle, buffer, width, coord, out var read) || read <= 0)
            return "";
        if (read < buffer.Length)
            Array.Clear(buffer, read, buffer.Length - read);
        var text = new string(buffer, 0, read).Replace("\0", "");
        return text.TrimEnd();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ConsoleScreenBufferInfo
    {
        public Coord dwSize;
        public Coord dwCursorPosition;
        public short wAttributes;
        public SmallRect srWindow;
        public Coord dwMaximumWindowSize;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out ConsoleScreenBufferInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ReadConsoleOutputCharacter(IntPtr handle, [Out] char[] buffer, int length, Coord coord, out int read);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetConsoleTitle(StringBuilder title, uint size);
}
