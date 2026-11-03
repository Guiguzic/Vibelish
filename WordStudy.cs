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

    public void Build(string text, Font font, int maxWidth)
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
        var lineStep = lineHeight + Math.Max(2, lineHeight / 6);
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
        Height = y + lineHeight;
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
