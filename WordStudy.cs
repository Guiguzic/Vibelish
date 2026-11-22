using System.Globalization;
using System.Text.Json;

namespace Vibelish;

sealed record WordSense(string Pos, string Chinese, bool Fits, string Note);

sealed record WordGloss(
    bool Ok,
    string Word,
    string Lemma,
    string Phonetic,
    IReadOnlyList<WordSense> Senses,
    string Tip,
    string Banner,
    bool CanRewrite,
    string Detail);

sealed record SenseRewrite(bool Ok, string English, string Chinese, string Detail);

sealed record StudyWord(string English, string Chinese, string Why);

/// <param name="Wrong">原文里写错的那一小段（照抄）。</param>
/// <param name="Right">改正后的写法。</param>
/// <param name="Type">grammar、word、collocation、literal（中式表达）、spelling、punctuation，见 <see cref="Coaching.Label"/>。</param>
/// <param name="Why">为什么错，用母语写的一句话。</param>
sealed record CoachFix(string Wrong, string Right, string Type, string Why);

/// <summary>大模型顺带给的学习反馈。</summary>
/// <param name="Fixes">反向（自己写的外语）时逐处纠错；空列表表示没发现错误。</param>
/// <param name="Habit">反向时，这些错误背后的母语思维，和外语该怎么想。</param>
/// <param name="Tip">正向时的写法点拨：译文和逐字直译哪里不一样。</param>
/// <param name="Parts">句子拆解：外语句子按主语、谓语、宾语……分成几块，每块一句大白话。</param>
/// <param name="Checked">这次请大模型纠错了（关了纠错只要拆解时为 false，卡片不说「没发现错误」）。</param>
sealed record Coaching(IReadOnlyList<CoachFix> Fixes, string Habit, string Tip, IReadOnlyList<SentencePart>? Parts = null, bool Checked = true)
{
    public IReadOnlyList<SentencePart> PartList => Parts ?? [];

    public static readonly string[] Types = ["grammar", "word", "collocation", "literal", "spelling", "punctuation"];

    /// <summary>错误类型的中文叫法。中文母语学英文时 literal 叫「中式英语」。</summary>
    public static string Label(string type, Language native, Language target) => type switch
    {
        "grammar" => "语法",
        "word" => "用词",
        "collocation" => "搭配",
        "literal" => Languages.IsChineseEnglish(native, target) ? "中式英语" : "母语直译",
        "spelling" => "拼写",
        "punctuation" => "标点",
        _ => "其他",
    };

    /// <summary>大模型写的类型名不一定规范：同义词归到六类里，认不出的算 other。</summary>
    public static string NormalizeType(string type)
    {
        var t = type.Trim().ToLowerInvariant();
        if (Array.IndexOf(Types, t) >= 0)
            return t;
        if (t.Contains("chinglish") || t.Contains("literal") || t.Contains("translat") || t.Contains("中式"))
            return "literal";
        if (t.Contains("colloc") || t.Contains("搭配"))
            return "collocation";
        if (t.Contains("spell") || t.Contains("拼写"))
            return "spelling";
        if (t.Contains("punct") || t.Contains("capital") || t.Contains("标点"))
            return "punctuation";
        if (t.Contains("word") || t.Contains("vocab") || t.Contains("usage") || t.Contains("用词"))
            return "word";
        if (t.Contains("gramm") || t.Contains("tense") || t.Contains("article") || t.Contains("preposition") || t.Contains("agreement")
            || t.Contains("order") || t.Contains("structure") || t.Contains("语法"))
            return "grammar";
        return "other";
    }
}

/// <summary>句子里的一块：原文、成分（subject、verb……）、这块在句子里干什么。</summary>
sealed record SentencePart(string Text, string Role, string Note)
{
    public static readonly string[] Roles =
    [
        "subject", "verb", "object", "complement", "adverbial", "attributive",
        "subject-clause", "object-clause", "attributive-clause", "adverbial-clause", "clause", "connector", "other",
    ];

    /// <summary>每种成分一个固定颜色，句子里的标色和下面的拆解列表用同一套。深色主题换亮一点的。</summary>
    public static Color ColorFor(string role, bool dark) => role switch
    {
        "subject" => dark ? Color.FromArgb(96, 165, 250) : Color.FromArgb(37, 99, 235),
        "verb" => dark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(220, 38, 38),
        "object" => dark ? Color.FromArgb(251, 191, 36) : Color.FromArgb(202, 118, 4),
        "complement" => dark ? Color.FromArgb(250, 204, 21) : Color.FromArgb(161, 98, 7),
        "adverbial" => dark ? Color.FromArgb(167, 139, 250) : Color.FromArgb(124, 58, 237),
        "attributive" => dark ? Color.FromArgb(45, 212, 191) : Color.FromArgb(13, 148, 136),
        "subject-clause" => dark ? Color.FromArgb(147, 197, 253) : Color.FromArgb(29, 78, 216),
        "object-clause" => dark ? Color.FromArgb(251, 146, 60) : Color.FromArgb(194, 65, 12),
        "attributive-clause" => dark ? Color.FromArgb(94, 234, 212) : Color.FromArgb(15, 118, 110),
        "adverbial-clause" => dark ? Color.FromArgb(192, 132, 252) : Color.FromArgb(147, 51, 234),
        "clause" => dark ? Color.FromArgb(244, 114, 182) : Color.FromArgb(219, 39, 119),
        _ => dark ? Color.FromArgb(156, 163, 175) : Color.FromArgb(107, 114, 128),
    };

    /// <summary>成分的叫法。中文母语用课本上的说法，其他母语用英文术语。</summary>
    public static string Label(string role, Language native) => native.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? role switch
        {
            "subject" => "主语",
            "verb" => "谓语",
            "object" => "宾语",
            "complement" => "补语",
            "adverbial" => "状语",
            "attributive" => "定语",
            "subject-clause" => "主语从句",
            "object-clause" => "宾语从句",
            "attributive-clause" => "定语从句",
            "adverbial-clause" => "状语从句",
            "clause" => "从句",
            "connector" => "连接词",
            _ => "其他",
        }
        : role switch
        {
            "other" => "other",
            _ => role,
        };

    /// <summary>小白模式下标签后面跟一句大白话：主语 = 谁 / 什么。</summary>
    public static string Plain(string role, Language native) => !native.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? ""
        : role switch
        {
            "subject" => "谁 / 什么在做",
            "verb" => "做什么、是什么",
            "object" => "动作落在谁身上",
            "complement" => "把意思补完整",
            "adverbial" => "什么时候、在哪、怎样、为什么",
            "attributive" => "修饰前后的名词",
            "subject-clause" => "一整个小句子当主语",
            "object-clause" => "一整个小句子当宾语，说的是「什么内容」",
            "attributive-clause" => "一整个小句子修饰前面的名词",
            "adverbial-clause" => "一整个小句子说明什么时候、为什么、在什么条件下",
            "clause" => "句子里套的小句子",
            "connector" => "把前后连起来",
            _ => "",
        };

    public static string NormalizeRole(string role)
    {
        var r = role.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (Array.IndexOf(Roles, r) >= 0)
            return r;
        if (r.Contains("clause") || r.Contains("从句"))
        {
            if (r.Contains("obj") || r.Contains("宾语") || r.Contains("noun"))
                return "object-clause";
            if (r.Contains("adverb") || r.Contains("状语"))
                return "adverbial-clause";
            if (r.Contains("attribut") || r.Contains("relative") || r.Contains("定语"))
                return "attributive-clause";
            if (r.Contains("subj") || r.Contains("主语"))
                return "subject-clause";
            return "clause";
        }

        if (r.Contains("subj") || r.Contains("主语"))
            return "subject";
        if (r.Contains("predicate") || r.Contains("verb") || r.Contains("谓语"))
            return "verb";
        if (r.Contains("obj") || r.Contains("宾语"))
            return "object";
        if (r.Contains("complement") || r.Contains("补语") || r.Contains("表语"))
            return "complement";
        if (r.Contains("adverb") || r.Contains("状语"))
            return "adverbial";
        if (r.Contains("attribut") || r.Contains("modifier") || r.Contains("定语"))
            return "attributive";
        if (r.Contains("conj") || r.Contains("connect") || r.Contains("连"))
            return "connector";
        return "other";
    }
}

/// <summary>
/// 把英文译文拆成可点的单词，并解析查词 / 改写接口返回的 JSON。
/// </summary>
static class WordStudy
{
    public readonly record struct Piece(string Text, bool Word);

    public static List<Piece> Split(string text)
    {
        var list = new List<Piece>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\r')
            {
                i++;
                continue;
            }

            if (ch == '\n')
            {
                list.Add(new Piece("\n", false));
                i++;
                continue;
            }

            if (IsLetter(ch))
            {
                var start = i;
                i++;
                while (i < text.Length)
                {
                    if (IsLetter(text[i]))
                    {
                        i++;
                        continue;
                    }

                    if ((text[i] == '\'' || text[i] == '\u2019' || text[i] == '-')
                        && i + 1 < text.Length
                        && IsLetter(text[i + 1]))
                    {
                        i += 2;
                        continue;
                    }

                    break;
                }

                list.Add(new Piece(text[start..i], true));
                continue;
            }

            var from = i;
            i++;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r' && !IsLetter(text[i]))
                i++;
            list.Add(new Piece(text[from..i], false));
        }

        return list;
    }

    public static WordGloss Fail(string word, string detail) =>
        new(false, word, word, "", Array.Empty<WordSense>(), "", "", false, detail);

    public static WordGloss ParseGloss(string word, string raw, bool canRewrite)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            var root = doc.RootElement;
            var lemma = ReadString(root, "lemma");
            if (lemma.Length == 0)
                lemma = word;
            var senses = new List<WordSense>();
            var senseArray = Prop(root, "senses");
            if (senseArray is { ValueKind: JsonValueKind.Array } sensesElement)
            {
                foreach (var item in sensesElement.EnumerateArray())
                {
                    var chinese = ReadString(item, "meaning");
                    if (chinese.Length == 0)
                        chinese = ReadString(item, "zh");
                    if (chinese.Length == 0)
                        chinese = ReadString(item, "chinese");
                    if (chinese.Length == 0)
                        continue;
                    senses.Add(new WordSense(
                        ReadString(item, "pos"),
                        chinese,
                        ReadBool(item, "fit"),
                        ReadString(item, "note")));
                    if (senses.Count == 4)
                        break;
                }
            }

            if (senses.Count == 0)
                return Fail(word, "没有返回释义");

            return new WordGloss(
                true,
                word,
                lemma,
                ReadString(root, "phonetic"),
                NormalizeFits(senses),
                ReadString(root, "tip"),
                "",
                canRewrite,
                "");
        }
        catch (Exception)
        {
            return Fail(word, "释义格式无法识别");
        }
    }

    public static WordGloss ParseDictionary(string word, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return Fail(word, "词典里没有这个词");

            var entry = doc.RootElement[0];
            var phonetic = ReadString(entry, "phonetic");
            if (phonetic.Length == 0 && Prop(entry, "phonetics") is { ValueKind: JsonValueKind.Array } phonetics)
            {
                foreach (var item in phonetics.EnumerateArray())
                {
                    phonetic = ReadString(item, "text");
                    if (phonetic.Length > 0)
                        break;
                }
            }

            var senses = new List<WordSense>();
            if (Prop(entry, "meanings") is { ValueKind: JsonValueKind.Array } meanings)
            {
                foreach (var meaning in meanings.EnumerateArray())
                {
                    var definition = "";
                    if (Prop(meaning, "definitions") is { ValueKind: JsonValueKind.Array } definitions)
                    {
                        foreach (var item in definitions.EnumerateArray())
                        {
                            definition = ReadString(item, "definition");
                            if (definition.Length > 0)
                                break;
                        }
                    }

                    if (definition.Length == 0)
                        continue;
                    senses.Add(new WordSense(ReadString(meaning, "partOfSpeech"), definition, false, definition));
                    if (senses.Count == 4)
                        break;
                }
            }

            if (senses.Count == 0)
                return Fail(word, "词典里没有这个词");

            return new WordGloss(
                true,
                word,
                ReadString(entry, "word").Length == 0 ? word : ReadString(entry, "word"),
                phonetic,
                NormalizeFits(senses),
                "",
                "还没配置模型接口，这里是英英释义。设置里填上接口后，可以看中文释义，并按义项改一句。",
                false,
                "");
        }
        catch (Exception)
        {
            return Fail(word, "词典暂时打不开");
        }
    }

    public static IReadOnlyList<StudyWord> ParseStudy(string raw)
    {
        var words = new List<StudyWord>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            if (Prop(doc.RootElement, "words") is not { } array || array.ValueKind != JsonValueKind.Array)
                return words;
            foreach (var item in array.EnumerateArray())
            {
                var english = ReadString(item, "word");
                if (english.Length == 0)
                    english = ReadString(item, "en");
                var chinese = ReadString(item, "meaning");
                if (chinese.Length == 0)
                    chinese = ReadString(item, "zh");
                if (chinese.Length == 0)
                    chinese = ReadString(item, "chinese");
                if (english.Length == 0 || chinese.Length == 0)
                    continue;
                if (english.Length > 40)
                    english = english[..40];
                words.Add(new StudyWord(english, chinese, ReadString(item, "why")));
                if (words.Count == 6)
                    break;
            }
        }
        catch (Exception)
        {
            return words;
        }

        return words;
    }

    /// <summary>英译中时大模型给的「更地道的说法」。原句已经很好时是空字符串；没给这一项（JSON 坏了）是 null。</summary>
    public static string? ParseBetter(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            if (Prop(doc.RootElement, "better") is not { } value)
                return null;
            return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>纠错、思维提示、写法点拨。JSON 里三项都没有（没要求，或格式坏了）时返回 null。</summary>
    public static Coaching? ParseCoach(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            var root = doc.RootElement;
            var fixesElement = Prop(root, "fixes");
            var hasHabit = Prop(root, "habit") != null;
            var hasTip = Prop(root, "tip") != null;
            var partsElement = Prop(root, "parts");
            if (fixesElement == null && !hasHabit && !hasTip && partsElement == null)
                return null;

            var fixes = new List<CoachFix>();
            if (fixesElement is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var wrong = ReadString(item, "wrong");
                    var right = ReadString(item, "right");
                    if (right.Length == 0)
                        right = ReadString(item, "correct");
                    // 原文和改法一样的不算错（大模型偶尔会把没改的也列出来）。
                    if (wrong.Length == 0 && right.Length == 0 || string.Equals(wrong, right, StringComparison.Ordinal))
                        continue;
                    fixes.Add(new CoachFix(Cut(wrong, 80), Cut(right, 80), Coaching.NormalizeType(ReadString(item, "type")), Cut(ReadString(item, "why"), 240)));
                    if (fixes.Count == 6)
                        break;
                }
            }

            var parts = new List<SentencePart>();
            if (partsElement is { ValueKind: JsonValueKind.Array } partArray)
            {
                foreach (var item in partArray.EnumerateArray())
                {
                    var text = ReadString(item, "text");
                    if (text.Length == 0)
                        continue;
                    parts.Add(new SentencePart(Cut(text, 120), SentencePart.NormalizeRole(ReadString(item, "role")), Cut(ReadString(item, "note"), 160)));
                    if (parts.Count == 10)
                        break;
                }
            }

            return new Coaching(fixes, Cut(ReadString(root, "habit"), 300), Cut(ReadString(root, "tip"), 300), parts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    public static SenseRewrite ParseRewrite(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            var english = ReadString(doc.RootElement, "english");
            var chinese = ReadString(doc.RootElement, "chinese");
            if (english.Length == 0)
                return new SenseRewrite(false, "", "", "没有返回改写");
            return new SenseRewrite(true, english, chinese, "");
        }
        catch (Exception)
        {
            var line = raw.Trim().Trim('"');
            if (line.Length == 0 || line.StartsWith('{') || line.StartsWith('`'))
                return new SenseRewrite(false, "", "", "改写格式无法识别");
            var first = line.Split('\n', 2)[0].Trim();
            if (first.Length == 0)
                return new SenseRewrite(false, "", "", "没有返回改写");
            return new SenseRewrite(true, first, "", "");
        }
    }

    public static string? SelfTest()
    {
        var pieces = Split("Don't stop the well-known plan.\nIt's ready.");
        var words = pieces.Where(piece => piece.Word).Select(piece => piece.Text).ToArray();
        if (words.Length != 7)
            return "split count " + words.Length;
        if (words[0] != "Don't" || words[3] != "well-known" || words[5] != "It's")
            return "split " + string.Join(",", words);

        var fenced = "```json\n{\"lemma\":\"implement\",\"phonetic\":\"/test/\",\"senses\":["
            + "{\"pos\":\"v.\",\"zh\":\"实施\",\"fit\":true,\"note\":\"本句\"},"
            + "{\"pos\":\"n.\",\"zh\":\"工具\",\"fit\":true,\"note\":\"另一义\"}"
            + "],\"tip\":\"做完\"}\n```";
        var gloss = ParseGloss("implemented", fenced, true);
        if (!gloss.Ok || gloss.Senses.Count != 2 || gloss.Lemma != "implement" || !gloss.CanRewrite)
            return "parse gloss";
        if (gloss.Senses.Count(sense => sense.Fits) != 1 || !gloss.Senses[0].Fits || gloss.Senses[1].Fits)
            return "fit flag";
        if (ParseGloss("word", "{}", true).Ok)
            return "empty senses should fail";

        var rewrite = ParseRewrite("{\"english\":\"They carried it out.\",\"chinese\":\"他们落实了。\"}");
        if (!rewrite.Ok || rewrite.English != "They carried it out." || rewrite.Chinese != "他们落实了。")
            return "parse rewrite";

        var study = ParseStudy("```json\n{\"words\":[{\"en\":\"repository\",\"zh\":\"仓库\",\"why\":\"代码仓库\"},{\"en\":\"the\",\"zh\":\"\"}]}\n```");
        if (study.Count != 1 || study[0].English != "repository" || study[0].Chinese != "仓库")
            return "parse study";

        var coach = ParseCoach("{\"fixes\":[{\"wrong\":\"go\",\"right\":\"went\",\"type\":\"Tense\",\"why\":\"yesterday 用过去时\"},"
            + "{\"wrong\":\"same\",\"right\":\"same\",\"type\":\"word\"},{\"wrong\":\"open the light\",\"right\":\"turn on the light\",\"type\":\"Chinglish\",\"why\":\"开灯不是 open\"}],"
            + "\"better\":\"x\",\"habit\":\"中文不变形，英文靠动词变化表示时间\"}");
        if (coach == null || coach.Fixes.Count != 2 || coach.Fixes[0].Type != "grammar" || coach.Fixes[1].Type != "literal" || coach.Habit.Length == 0)
            return "parse coach";
        var parsed = ParseCoach("{\"tip\":\"\",\"parts\":[{\"text\":\"We\",\"role\":\"Subject\",\"note\":\"谁：我们\"},{\"text\":\"\",\"role\":\"verb\"},{\"text\":\"have implemented\",\"role\":\"predicate\",\"note\":\"做了什么\"},{\"text\":\"already\",\"role\":\"状语\"}]}");
        if (parsed == null || parsed.PartList.Count != 3 || parsed.PartList[0].Role != "subject" || parsed.PartList[1].Role != "verb" || parsed.PartList[2].Role != "adverbial")
            return "parse parts";
        if (SentencePart.NormalizeRole("object clause") != "object-clause" || SentencePart.NormalizeRole("定语从句") != "attributive-clause"
            || SentencePart.NormalizeRole("Adverbial_Clause") != "adverbial-clause" || SentencePart.NormalizeRole("subject") != "subject")
            return "normalize roles";
        if (ParseCoach("{\"better\":\"x\"}") != null || ParseCoach("{\"fixes\":[]}")?.Fixes.Count != 0 || ParseCoach("{\"tip\":\"换主语\"}")?.Tip != "换主语")
            return "parse coach empty";

        var dict = ParseDictionary("set", """
            [{"word":"set","phonetic":"/set/","meanings":[
              {"partOfSpeech":"verb","definitions":[{"definition":"put something somewhere"}]},
              {"partOfSpeech":"noun","definitions":[{"definition":"a group of things"}]}
            ]}]
            """);
        if (!dict.Ok || dict.CanRewrite || dict.Senses.Count != 2 || dict.Phonetic != "/set/" || !dict.Senses[0].Fits)
            return "parse dictionary";

        using var font = new Font("Microsoft YaHei UI", 15.5F, FontStyle.Bold);
        var layout = new EnglishLayout();
        layout.Build("Hello, world", font, 600);
        var runs = layout.Runs.Where(run => run.Word).ToArray();
        if (runs.Length != 2 || runs[0].Text != "Hello" || runs[1].Text != "world")
            return "layout words";
        if (runs[0].Bounds.Right > runs[1].Bounds.Left)
            return "layout overlap";
        if (layout.WordAt(new Point(runs[0].Bounds.Left + 2, runs[0].Bounds.Top + 2)) != "Hello")
            return "hit hello";
        if (layout.WordAt(new Point(runs[1].Bounds.Left + 2, runs[1].Bounds.Top + 2)) != "world")
            return "hit world";
        if (layout.WordAt(new Point(2, layout.Height + 30)) != null)
            return "hit outside";

        layout.Build(string.Join(' ', Enumerable.Repeat("language", 12)), font, 90);
        if (layout.Runs.Count(run => run.Word && run.Bounds.Y > 0) < 2)
            return "wrap";
        var lower = layout.Runs.First(run => run.Word && run.Bounds.Y > 0);
        if (layout.WordAt(new Point(lower.Bounds.Left + 1, lower.Bounds.Top + 2)) != "language")
            return "hit wrapped";
        return null;
    }

    static List<WordSense> NormalizeFits(List<WordSense> senses)
    {
        var fit = senses.FindIndex(sense => sense.Fits);
        if (fit < 0)
            fit = 0;
        var normalized = new List<WordSense>(senses.Count);
        for (var i = 0; i < senses.Count; i++)
            normalized.Add(senses[i] with { Fits = i == fit });
        return normalized;
    }

    static string ExtractJson(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = raw.IndexOf('\n');
            if (newline >= 0)
                raw = raw[(newline + 1)..];
            var fence = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                raw = raw[..fence];
            raw = raw.Trim();
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
            raw = raw[start..(end + 1)];
        return raw.Trim();
    }

    static string ReadString(JsonElement item, string name)
    {
        if (Prop(item, name) is not { } value || value.ValueKind != JsonValueKind.String)
            return "";
        return (value.GetString() ?? "").Trim();
    }

    static bool ReadBool(JsonElement item, string name)
    {
        if (Prop(item, name) is not { } value)
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    static JsonElement? Prop(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    /// <summary>能组成可点单词的字母：拉丁（含带重音的）、西里尔、韩文等用空格分词的文字。
    /// 汉字、假名、泰文不分词，不算。</summary>
    static bool IsLetter(char ch)
    {
        if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            return true;
        if (ch < 'À' || ch is >= '　' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯'
            or >= '฀' and <= '๿')
            return false;
        return char.IsLetter(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }
}

sealed class EnglishLayout
{
    public readonly record struct Run(string Text, bool Word, Rectangle Bounds);

    public List<Run> Runs { get; } = new();
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <param name="labelSpace">每行下面多留的高度，用来写成分标签（主语、谓语……）。</param>
    public void Build(string text, Font font, int maxWidth, int labelSpace = 0)
    {
        Runs.Clear();
        Width = 0;
        Height = 0;
        if (font == null)
            return;
        if (text.Length == 0 || maxWidth < 8)
        {
            Height = font.Height;
            return;
        }

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        // 单独量一个空格会得到 18 像素这种离谱的值，单词也会多出几像素的悬垂。
        // 夹在两根竖线中间量，再减掉两根竖线，得到的才是真实的步进宽度。
        var bars = TextRenderer.MeasureText("||", font, new Size(int.MaxValue, int.MaxValue), flags).Width;
        // 中文按字拆开以后同一个字会量很多次，记下来。
        var widths = new Dictionary<string, int>(StringComparer.Ordinal);
        int MeasureWidth(string piece)
        {
            if (!widths.TryGetValue(piece, out var width))
                widths[piece] = width = Math.Max(1, TextRenderer.MeasureText("|" + piece + "|", font, new Size(int.MaxValue, int.MaxValue), flags).Width - bars);
            return width;
        }
        var lineHeight = Math.Max(
            font.Height,
            TextRenderer.MeasureText("Ag", font, new Size(int.MaxValue, int.MaxValue), flags).Height);
        var lineStep = lineHeight + Math.Max(2, lineHeight / 6) + labelSpace;
        var x = 0;
        var y = 0;
        var maxX = 0;
        var pieces = SplitCjk(WordStudy.Split(text));
        for (var p = 0; p < pieces.Count; p++)
        {
            var piece = pieces[p];
            if (piece.Text == "\n")
            {
                y += lineStep;
                x = 0;
                continue;
            }

            var pieceText = piece.Text;
            var width = MeasureWidth(pieceText);

            // 单词后面紧跟的标点（"word," 里的逗号）不能落到下一行行首，换行时连在一起算。
            var glue = 0;
            if (piece.Word && p + 1 < pieces.Count && !pieces[p + 1].Word)
            {
                var next = pieces[p + 1].Text;
                var lead = 0;
                while (lead < next.Length && !char.IsWhiteSpace(next[lead]))
                    lead++;
                if (lead > 0)
                    glue = MeasureWidth(next[..lead]);
            }

            var attached = !piece.Word && pieceText.Length > 0 && !char.IsWhiteSpace(pieceText[0]) && p > 0
                && (pieces[p - 1].Word || Closing.Contains(pieceText[0]));
            var wraps = !attached && x > 0 && x + width + glue > maxWidth;
            if (wraps && !piece.Word)
            {
                pieceText = pieceText.TrimStart();
                y += lineStep;
                x = 0;
                if (pieceText.Length == 0)
                    continue;
                width = MeasureWidth(pieceText);
            }
            else if (wraps)
            {
                y += lineStep;
                x = 0;
            }

            Runs.Add(new Run(pieceText, piece.Word, new Rectangle(x, y, width, lineHeight)));
            x += width;
            if (x > maxX)
                maxX = x;
        }

        Width = maxX;
        Height = y + lineHeight + labelSpace;
    }

    /// <summary>行首不能出现的标点（中文的逗号、句号这些也算）。</summary>
    const string Closing = "，。、；：！？）」』”’》〉】,.;:!?)";

    /// <summary>中文没有空格：英译中的译文要按字拆开，才能在任意两个字之间换行。</summary>
    static List<WordStudy.Piece> SplitCjk(List<WordStudy.Piece> pieces)
    {
        // 汉字、假名、全角标点，还有泰文（不用空格分词）。泰文的元音符号要跟着前面的辅音，按字形簇拆。
        static bool Wide(char ch) => ch is >= (char)0x3000 and <= (char)0x9FFF or >= (char)0xF900 and <= (char)0xFAFF
            or >= (char)0xFF00 and <= (char)0xFFEF or >= (char)0x0E00 and <= (char)0x0E7F;

        var list = new List<WordStudy.Piece>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (piece.Word || !piece.Text.Any(Wide))
            {
                list.Add(piece);
                continue;
            }

            var text = piece.Text;
            var starts = StringInfo.ParseCombiningCharacters(text);
            var start = 0;
            for (var e = 0; e < starts.Length; e++)
            {
                var at = starts[e];
                if (!Wide(text[at]))
                    continue;
                var end = e + 1 < starts.Length ? starts[e + 1] : text.Length;
                if (at > start)
                    list.Add(new WordStudy.Piece(text[start..at], false));
                list.Add(new WordStudy.Piece(text[at..end], false));
                start = end;
            }

            if (start < text.Length)
                list.Add(new WordStudy.Piece(text[start..], false));
        }

        return list;
    }

    public string? WordAt(Point point)
    {
        foreach (var run in Runs)
        {
            if (!run.Word)
                continue;
            var hit = run.Bounds;
            hit.Inflate(1, 3);
            if (hit.Contains(point))
                return run.Text;
        }

        return null;
    }

    public int IndexAt(Point point)
    {
        for (var i = 0; i < Runs.Count; i++)
        {
            if (!Runs[i].Word)
                continue;
            var hit = Runs[i].Bounds;
            hit.Inflate(1, 3);
            if (hit.Contains(point))
                return i;
        }

        return -1;
    }
}
