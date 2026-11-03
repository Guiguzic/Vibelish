using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace InputTranslate;

sealed class AppConfig
{
    public const string DefaultHotkey = "Ctrl+Alt+T";

    /// <summary>是否注册全局热键。关掉后仍可以从托盘菜单开始。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>auto（配置了大模型接口就用它，否则用免费引擎）、openai、transmart、mymemory、google。</summary>
    public string Engine { get; set; } = "auto";

    public string OpenAiBaseUrl { get; set; } = "";

    /// <summary>内存里是明文。保存时写进 <see cref="OpenAiApiKeyProtected"/>，这里留空。
    /// 用户手动在 config.json 里填明文也能读到，下次保存时会改成加密保存。</summary>
    public string OpenAiApiKey { get; set; } = "";

    /// <summary>用 Windows DPAPI（当前用户）加密的密钥，Base64。</summary>
    public string OpenAiApiKeyProtected { get; set; } = "";

    public string OpenAiModel { get; set; } = "";

    /// <summary>输完停顿多久开始翻译。快 350 / 适中 600 / 稳 1000。</summary>
    public int PauseMs { get; set; } = 600;
    public bool ChineseOnly { get; set; } = true;
    public bool AutoCopy { get; set; }
    public bool AutoWrite { get; set; }

    /// <summary>译完自动挑出值得记的词（需要大模型接口）。</summary>
    public bool StudyWords { get; set; } = true;

    /// <summary>kaoyan (考研英语), cet6 (六级) or plain (日常).</summary>
    public string EnglishLevel { get; set; } = "kaoyan";

    /// <summary>例如 Ctrl+Alt+T。被占用时会自动换成备用组合。</summary>
    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>system、dark 或 light。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>small、normal 或 large。</summary>
    public string CardTextSize { get; set; } = "normal";

    /// <summary>强调色：blue、purple、pink、orange、green、graphite。</summary>
    public string Accent { get; set; } = "blue";

    /// <summary>大模型逐字显示译文（流式输出），第一个词通常不到一秒就出来。</summary>
    public bool StreamOutput { get; set; } = true;

    /// <summary>对支持的服务关闭「深度思考」，翻译快很多。</summary>
    public bool FastThinking { get; set; } = true;

    /// <summary>卡片上显示中文原文。</summary>
    public bool ShowSource { get; set; } = true;

    /// <summary>卡片上显示操作提示（Enter 完成 · Esc 取消 等）。</summary>
    public bool ShowHints { get; set; } = true;

    /// <summary>卡片上显示译文来源（模型名、引擎）。</summary>
    public bool ShowEngine { get; set; } = true;

    /// <summary>重点词怎么显示：word（只有单词）、meaning（单词 + 中文释义）、detail（再加一句说明）。</summary>
    public string StudyStyle { get; set; } = "meaning";

    /// <summary>点英文里的单词查释义。</summary>
    public bool ClickLookup { get; set; } = true;

    /// <summary>卡片上显示朗读按钮。</summary>
    public bool ShowSpeak { get; set; } = true;

    /// <summary>查词时自动读一遍单词。</summary>
    public bool SpeakOnLookup { get; set; }

    /// <summary>朗读语速：slow、normal、fast。用 Windows 自带的英文语音。</summary>
    public string SpeakSpeed { get; set; } = "normal";

    /// <summary>卡片位置：input（跟着输入框）、top（屏幕顶部）、bottom（屏幕底部）。</summary>
    public string CardPosition { get; set; } = "input";

    /// <summary>按 Enter 完成后几秒自动收起卡片，0 表示不收起。</summary>
    public int AutoHideSeconds { get; set; }

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InputTranslate");

    public static string FilePath => Path.Combine(Directory, "config.json");

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        AppConfig config;
        try
        {
            if (!File.Exists(FilePath))
                return new AppConfig().Normalize();
            var json = File.ReadAllText(FilePath);
            config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // 配置坏了也要能启动；留一份副本，免得下次保存时把用户的设置覆盖掉。
            TraceLog.Note("config unreadable " + ex.GetType().Name);
            try
            {
                File.Copy(FilePath, Path.Combine(Directory, "config.bad.json"), true);
            }
            catch
            {
                // Ignore.
            }

            return new AppConfig().Normalize();
        }

        if (config.OpenAiApiKey.Trim().Length == 0 && config.OpenAiApiKeyProtected.Length > 0)
            config.OpenAiApiKey = Unprotect(config.OpenAiApiKeyProtected);
        return config.Normalize();
    }

    /// <summary>写到临时文件再替换，写到一半断电也不会留下半个 JSON。</summary>
    public bool TrySave(out string error)
    {
        error = "";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var copy = Clone();
            var key = OpenAiApiKey.Trim();
            var protectedKey = key.Length == 0 ? "" : Protect(key);
            if (key.Length == 0 || protectedKey.Length > 0)
            {
                copy.OpenAiApiKey = "";
                copy.OpenAiApiKeyProtected = protectedKey;
            }
            else
            {
                // DPAPI 不可用时退回明文，至少不丢配置。
                copy.OpenAiApiKeyProtected = "";
            }

            var json = JsonSerializer.Serialize(copy, WriteOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, FilePath, true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TraceLog.Note("config save fail " + TraceLog.Safe(ex.Message));
            return false;
        }
    }

    /// <summary>把手改出错的值拉回到能用的范围。</summary>
    public AppConfig Normalize()
    {
        Engine = Pick(Engine, "auto", "auto", "openai", "transmart", "mymemory", "google");
        EnglishLevel = Pick(EnglishLevel, "kaoyan", "kaoyan", "cet6", "plain");
        Theme = Pick(Theme, "system", "system", "dark", "light");
        CardTextSize = Pick(CardTextSize, "normal", "small", "normal", "large");
        Accent = Pick(Accent, "blue", "blue", "purple", "pink", "orange", "green", "graphite");
        StudyStyle = Pick(StudyStyle, "meaning", "word", "meaning", "detail");
        SpeakSpeed = Pick(SpeakSpeed, "normal", "slow", "normal", "fast");
        CardPosition = Pick(CardPosition, "input", "input", "top", "bottom");
        AutoHideSeconds = AutoHideSeconds <= 0 ? 0 : Math.Clamp(AutoHideSeconds, 3, 120);
        PauseMs = Math.Clamp(PauseMs, 200, 3000);
        OpenAiBaseUrl = (OpenAiBaseUrl ?? "").Trim();
        OpenAiApiKey = (OpenAiApiKey ?? "").Trim();
        OpenAiApiKeyProtected = (OpenAiApiKeyProtected ?? "").Trim();
        OpenAiModel = (OpenAiModel ?? "").Trim();
        if (!HotkeySpec.TryParse(Hotkey, out var hotkey))
            hotkey = HotkeySpec.Parse(DefaultHotkey);
        Hotkey = hotkey.ToString();
        return this;
    }

    /// <summary>接口地址和模型都填了，并且有密钥（本机 / 局域网的服务可以不要密钥）。</summary>
    public bool HasChatApi =>
        OpenAiBaseUrl.Length > 0
        && OpenAiModel.Length > 0
        && (OpenAiApiKey.Length > 0 || IsLocalEndpoint(OpenAiBaseUrl));

    /// <summary>这次翻译实际会不会走大模型（难度设置只对它生效）。</summary>
    public bool UsesChatForTranslation => Engine == "openai" || (Engine == "auto" && HasChatApi);

    public static bool IsLocalEndpoint(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!System.Net.IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public string LevelLabel => EnglishLevel switch
    {
        "cet6" => "六级",
        "plain" => "日常",
        _ => "考研",
    };

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    static string Pick(string? value, string fallback, params string[] allowed)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(allowed, normalized) >= 0 ? normalized : fallback;
    }

    // DPAPI：密钥只有当前 Windows 用户能解开，拷走 config.json 也读不到。
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("InputTranslate.OpenAiApiKey");

    static string Protect(string plain)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(plain);
            return Convert.ToBase64String(Dpapi.Run(data, Entropy, protect: true));
        }
        catch
        {
            return "";
        }
    }

    static string Unprotect(string protectedBase64)
    {
        try
        {
            var data = Convert.FromBase64String(protectedBase64);
            return Encoding.UTF8.GetString(Dpapi.Run(data, Entropy, protect: false));
        }
        catch
        {
            TraceLog.Note("config key unprotect failed");
            return "";
        }
    }

    static class Dpapi
    {
        const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            var input = Pin(data);
            var salt = Pin(entropy);
            try
            {
                var ok = protect
                    ? CryptProtectData(ref input, "InputTranslate", ref salt, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok)
                    throw new InvalidOperationException("DPAPI " + Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
                Marshal.FreeHGlobal(salt.Data);
            }
        }

        static Blob Pin(byte[] bytes)
        {
            var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }
    }
}
