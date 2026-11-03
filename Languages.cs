namespace Vibelish;

/// <summary>一种语言用什么文字写。自动判断翻译方向时先看文字系统，同是拉丁字母再看常用词。</summary>
enum Script
{
    Latin,
    Han,
    Japanese,
    Hangul,
    Cyrillic,
    Thai,
}

/// <param name="Code">配置里存的代码，也是 Google / MyMemory 用的代码。</param>
/// <param name="Label">设置里语言列表显示的名字。</param>
/// <param name="Name">句子里用的名字，例如「输入英文」。</param>
/// <param name="Short">一两个字的简称，用在「中→英」「中译英」这种地方。</param>
/// <param name="English">写进提示词里的英文名。</param>
/// <param name="Transmart">腾讯交互翻译的语言代码；不支持就是空字符串，直接用 MyMemory / Google。</param>
/// <param name="Spaced">词和词之间有空格，能点单词查释义。中文、日文、泰文只能从重点词里点。</param>
/// <param name="Culture">找朗读语音用的区域。</param>
/// <param name="Font">卡片上显示这种语言用的字体；空字符串表示用英文字体（Segoe UI）。</param>
/// <param name="Common">常用词。两种语言都用拉丁字母时靠它们分辨。</param>
sealed record Language(
    string Code,
    string Label,
    string Name,
    string Short,
    string English,
    Script Script,
    string Transmart,
    bool Spaced,
    string Culture,
    string Font,
    string[] Common);

static class Languages
{
    public static readonly Language[] All =
    [
        new("zh-CN", "简体中文", "中文", "中", "Simplified Chinese", Script.Han, "zh", false, "zh-CN", "Microsoft YaHei UI", []),
        new("en", "英文 English", "英文", "英", "English", Script.Latin, "en", true, "en-US", "",
            ["the", "a", "an", "is", "are", "was", "were", "be", "to", "of", "and", "in", "on", "for", "with", "this", "that", "it",
             "you", "i", "my", "me", "please", "can", "could", "would", "should", "have", "has", "not", "do", "does", "what", "how", "why",
             "fix", "make", "add", "use", "help", "let's", "it's", "don't", "i'm"]),
        new("ja", "日文 日本語", "日文", "日", "Japanese", Script.Japanese, "ja", false, "ja-JP", "Yu Gothic UI", []),
        new("ko", "韩文 한국어", "韩文", "韩", "Korean", Script.Hangul, "ko", true, "ko-KR", "Malgun Gothic", []),
        new("fr", "法文 Français", "法文", "法", "French", Script.Latin, "fr", true, "fr-FR", "",
            ["le", "la", "les", "un", "une", "des", "est", "et", "je", "tu", "il", "elle", "nous", "vous", "pas", "pour", "dans", "que",
             "qui", "avec", "sur", "ce", "cette", "mon", "ma", "du", "au", "c'est", "j'ai", "très", "être", "faire", "merci"]),
        new("de", "德文 Deutsch", "德文", "德", "German", Script.Latin, "de", true, "de-DE", "",
            ["der", "die", "das", "und", "ist", "ich", "du", "nicht", "ein", "eine", "mit", "zu", "für", "auf", "den", "dem", "es",
             "wir", "sie", "bitte", "kann", "auch", "sind", "noch", "nur", "wie", "was", "mein", "danke"]),
        new("es", "西班牙文 Español", "西班牙文", "西", "Spanish", Script.Latin, "es", true, "es-ES", "",
            ["el", "la", "los", "las", "un", "una", "es", "y", "que", "de", "en", "por", "para", "con", "no", "mi", "yo", "tú",
             "está", "del", "lo", "se", "muy", "pero", "cómo", "qué", "gracias", "también"]),
        new("ru", "俄文 Русский", "俄文", "俄", "Russian", Script.Cyrillic, "ru", true, "ru-RU", "", []),
        new("pt", "葡萄牙文 Português", "葡萄牙文", "葡", "Portuguese", Script.Latin, "pt", true, "pt-BR", "",
            ["o", "os", "as", "um", "uma", "é", "e", "que", "de", "em", "para", "com", "não", "meu", "eu", "você", "está", "do",
             "da", "isso", "muito", "obrigado", "também", "mas", "como"]),
        new("it", "意大利文 Italiano", "意大利文", "意", "Italian", Script.Latin, "it", true, "it-IT", "",
            ["il", "lo", "gli", "le", "un", "una", "è", "e", "che", "di", "in", "per", "con", "non", "mio", "io", "tu", "sono",
             "del", "della", "questo", "grazie", "anche", "ma", "come"]),
        new("vi", "越南文 Tiếng Việt", "越南文", "越", "Vietnamese", Script.Latin, "vi", true, "vi-VN", "",
            ["là", "và", "của", "có", "không", "tôi", "bạn", "này", "được", "một", "cho", "với", "những", "các", "đã", "sẽ",
             "người", "trong", "cảm", "ơn"]),
        new("th", "泰文 ไทย", "泰文", "泰", "Thai", Script.Thai, "th", false, "th-TH", "Leelawadee UI", []),
        new("id", "印尼文 Bahasa Indonesia", "印尼文", "印尼", "Indonesian", Script.Latin, "id", true, "id-ID", "",
            ["yang", "dan", "di", "ini", "itu", "dengan", "untuk", "tidak", "saya", "anda", "ada", "akan", "dari", "ke", "bisa",
             "tolong", "sudah", "juga", "terima", "kasih"]),
        new("zh-TW", "繁体中文 繁體中文", "繁体中文", "繁", "Traditional Chinese", Script.Han, "", false, "zh-TW", "Microsoft JhengHei UI", []),
    ];

    public static Language Find(string? code) =>
        All.FirstOrDefault(language => string.Equals(language.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>认识的代码原样返回（大小写规范好），不认识的换成 fallback。</summary>
    public static string Normalize(string? code, string fallback)
    {
        var match = All.FirstOrDefault(language => string.Equals(language.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? fallback;
    }

    public static int IndexOf(string code) => Array.FindIndex(All, language => language.Code == code);

    /// <summary>中文母语学英文：难度才叫考研、六级。</summary>
    public static bool IsChineseEnglish(Language native, Language target) =>
        native.Script == Script.Han && target.Code == "en";
}
