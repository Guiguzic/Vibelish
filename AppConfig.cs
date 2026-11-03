using System.Text.Json;

namespace InputTranslate;

sealed class AppConfig
{
    public bool Enabled { get; set; } = true;
    public string Engine { get; set; } = "auto";
    public string OpenAiBaseUrl { get; set; } = "http://192.168.1.40/v1";
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "grok-4.5";
    public int PauseMs { get; set; } = 800;
    public bool ChineseOnly { get; set; } = true;
    public bool AutoCopy { get; set; }
    public bool AutoWrite { get; set; }

    /// <summary>kaoyan (考研英语), cet6 (六级) or plain (日常).</summary>
    public string EnglishLevel { get; set; } = "kaoyan";

    public static string FilePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InputTranslate");
            return Path.Combine(dir, "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppConfig();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    public AppConfig Clone()
    {
        return new AppConfig
        {
            Enabled = Enabled,
            Engine = Engine,
            OpenAiBaseUrl = OpenAiBaseUrl,
            OpenAiApiKey = OpenAiApiKey,
            OpenAiModel = OpenAiModel,
            PauseMs = PauseMs,
            ChineseOnly = ChineseOnly,
            AutoCopy = AutoCopy,
            AutoWrite = AutoWrite,
            EnglishLevel = EnglishLevel,
        };
    }
}
