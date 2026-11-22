using System.Runtime.InteropServices;

namespace Vibelish;

/// <summary>
/// 输入框不能直接改（终端、Docker 里的程序、微信这类自绘输入框）时，模拟按键把原文换成译文：
/// 终端按退格删掉原文；其他程序先 Ctrl+A 全选输入框。然后逐字发 Unicode（远程窗口改用粘贴，
/// 远程那边不一定认 Unicode 按键）。发出去的按键带「注入」标记，自己的键盘钩子会放过。
/// </summary>
static class KeyboardWriter
{
    public enum Erase
    {
        /// <summary>按 Ctrl+A 全选，再输入，选中的内容被替换。</summary>
        SelectAll,
        /// <summary>按退格删掉原文的字数（终端里没有「全选输入」）。</summary>
        Backspace,
    }

    const ushort VkBack = 0x08;
    const ushort VkReturn = 0x0D;
    const ushort VkShift = 0x10;
    const ushort VkControl = 0x11;
    const ushort VkLShift = 0xA0;
    const ushort VkRShift = 0xA1;
    const ushort VkLControl = 0xA2;
    const ushort VkRControl = 0xA3;
    const ushort VkA = 0x41;
    const ushort VkV = 0x56;
    const uint KeyUp = 0x0002;
    const uint Unicode = 0x0004;

    /// <param name="eraseCount">按退格时删几个字（原文长度）。</param>
    /// <param name="paste">用剪贴板粘贴代替逐字输入（远程窗口）。调用方先把文字放进剪贴板。</param>
    public static bool Replace(string text, Erase erase, int eraseCount, bool paste)
    {
        var inputs = new List<Input>();
        // 用户还按着 Ctrl+Shift：先松开，不然退格会变成 Ctrl+Shift+退格（删一个词）。
        foreach (var vk in new[] { VkLControl, VkRControl, VkLShift, VkRShift, VkControl, VkShift })
            inputs.Add(Key(vk, up: true));

        if (erase == Erase.SelectAll)
        {
            inputs.Add(Key(VkControl, false));
            inputs.Add(Key(VkA, false));
            inputs.Add(Key(VkA, true));
            inputs.Add(Key(VkControl, true));
        }
        else
        {
            for (var i = 0; i < eraseCount; i++)
            {
                inputs.Add(Key(VkBack, false));
                inputs.Add(Key(VkBack, true));
            }
        }

        if (paste)
        {
            inputs.Add(Key(VkControl, false));
            inputs.Add(Key(VkV, false));
            inputs.Add(Key(VkV, true));
            inputs.Add(Key(VkControl, true));
        }
        else
        {
            foreach (var ch in text.Replace("\r\n", "\n"))
            {
                if (ch == '\n')
                {
                    // 聊天框、终端里回车是发送；换行用 Shift+回车。
                    inputs.Add(Key(VkShift, false));
                    inputs.Add(Key(VkReturn, false));
                    inputs.Add(Key(VkReturn, true));
                    inputs.Add(Key(VkShift, true));
                    continue;
                }

                inputs.Add(Char(ch, false));
                inputs.Add(Char(ch, true));
            }
        }

        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
        return sent == array.Length;
    }

    static Input Key(ushort vk, bool up) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeybdInput { Vk = vk, Flags = up ? KeyUp : 0 } },
    };

    static Input Char(char ch, bool up) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeybdInput { Scan = ch, Flags = Unicode | (up ? KeyUp : 0) } },
    };

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    // 联合体要和最大的成员（MOUSEINPUT）一样大，SendInput 才认 cbSize。
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>在当前光标位置补一次右键单击（右键手势没拖动时，把吞掉的右键还回去）。</summary>
    public static void RightClick()
    {
        var inputs = new[]
        {
            new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0008 } } },
            new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0010 } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, Input[] inputs, int size);
}
