using System.Drawing.Imaging;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Automation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Vibelish;

static class OcrReader
{
    static readonly string[] Noise =
    [
        "发送", "发消息", "发送消息", "表情", "文件", "截图", "语音", "视频",
    ];

    public static async Task<string> ReadAsync(FocusSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Element == null || !CanCapture(snapshot.Bounds))
            return "";

        using var bitmap = CaptureElement(snapshot.Element);
        return await RecognizeAsync(bitmap);
    }

    public static string Clean(string text, string controlName)
    {
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !IsNoise(line))
            .ToList();
        var joined = string.Join("\n", lines).Trim();
        if (joined.Length == 0)
            return "";
        if (!string.IsNullOrWhiteSpace(controlName) && string.Equals(joined, controlName.Trim(), StringComparison.Ordinal))
            return "";
        return joined;
    }

    public static async Task<string> ProbeWeChatAsync()
    {
        if (!TryFindWeChatInput(out var element, out var bounds, out var control) || element == null)
            return "NO_INPUT";

        var hwnd = Native.FindHwnd(element);
        GetWindowRect(hwnd, out var windowRect);
        var title = new StringBuilder(256);
        _ = GetWindowText(hwnd, title, title.Capacity);
        var log = "hwnd=" + hwnd + " title=" + title
            + "\nwin=" + windowRect.Left + "," + windowRect.Top + " " + (windowRect.Right - windowRect.Left) + "x" + (windowRect.Bottom - windowRect.Top)
            + "\ninput=" + bounds.X + "," + bounds.Y + " " + bounds.Width + "x" + bounds.Height;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "wechat-ocr-log.txt"), log);
        using var bitmap = CaptureElement(element);
        var path = Path.Combine(Path.GetTempPath(), "wechat-ocr.png");
        bitmap.Save(path, ImageFormat.Png);
        var text = Clean(await RecognizeAsync(bitmap), control);
        return log + "\nimage=" + path + "\ntext=" + text;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    public static bool CanCapture(Rectangle bounds)
    {
        return bounds.Width is >= 40 and <= 2200 && bounds.Height is >= 24 and <= 420;
    }

    static bool TryFindWeChatInput(out AutomationElement? element, out Rectangle bounds, out string control)
    {
        element = null;
        bounds = Rectangle.Empty;
        control = "";
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Weixin"))
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero)
                    continue;
                var root = AutomationElement.FromHandle(process.MainWindowHandle);
                var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "chat_input_field");
                var found = root.FindFirst(TreeScope.Descendants, condition);
                if (found == null)
                    continue;
                var rect = found.Current.BoundingRectangle;
                bounds = Rectangle.Round(new RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height));
                control = found.Current.Name ?? "";
                element = found;
                return CanCapture(bounds);
            }
            catch
            {
                continue;
            }
        }

        return false;
    }

    static Bitmap CaptureElement(AutomationElement element)
    {
        var hwnd = Native.FindHwnd(element);
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var windowRect))
            throw new InvalidOperationException("找不到输入框所在窗口");

        var windowWidth = windowRect.Right - windowRect.Left;
        var windowHeight = windowRect.Bottom - windowRect.Top;
        var input = element.Current.BoundingRectangle;
        var window = AutomationElement.FromHandle(hwnd).Current.BoundingRectangle;
        var scaleX = window.Width > 1 ? windowWidth / window.Width : 1;
        var scaleY = window.Height > 1 ? windowHeight / window.Height : 1;
        var cropX = (int)Math.Round((input.X - window.X) * scaleX);
        var cropY = (int)Math.Round((input.Y - window.Y) * scaleY);
        var cropW = Math.Max(1, (int)Math.Round(input.Width * scaleX));
        var cropH = Math.Max(1, (int)Math.Round(input.Height * scaleY));
        cropX = Math.Clamp(cropX, 0, Math.Max(0, windowWidth - 1));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, windowHeight - 1));
        cropW = Math.Min(cropW, windowWidth - cropX);
        cropH = Math.Min(cropH, windowHeight - cropY);

        var full = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(full))
        {
            var hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, hdc, 2))
                    PrintWindow(hwnd, hdc, 0);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        var crop = full.Clone(new Rectangle(cropX, cropY, cropW, cropH), PixelFormat.Format32bppArgb);
        full.Dispose();
        return crop;
    }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    static OcrEngine? _engine;
    static bool _engineTried;

    // 识别器创建一次就够了；以前每 600 毫秒新建一个。
    static OcrEngine? Engine()
    {
        if (_engineTried)
            return _engine;
        _engineTried = true;
        try
        {
            _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"))
                ?? OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            _engine = null;
        }

        return _engine;
    }

    static async Task<string> RecognizeAsync(Bitmap bitmap)
    {
        var engine = Engine();
        if (engine == null)
            return "";

        using var software = ToSoftwareBitmap(bitmap);
        var result = await engine.RecognizeAsync(software);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }

    static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var packed = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + (y * data.Stride), packed, y * width * 4, width * 4);
            var software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            software.CopyFromBuffer(packed.AsBuffer());
            return software;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    static bool IsNoise(string line)
    {
        if (line.Length <= 1)
            return true;
        foreach (var word in Noise)
        {
            if (string.Equals(line, word, StringComparison.Ordinal))
                return true;
        }

        return line.All(ch => char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsWhiteSpace(ch));
    }
}
