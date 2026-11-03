using System.Globalization;

namespace Vibelish;

/// <summary>看一段文字更像母语还是在学的语言，决定往哪个方向翻。</summary>
static class Lang
{
    /// <summary>一段文字里各种文字系统各有多少：汉字、假名、韩文、泰文按字数，拉丁字母、西里尔字母按词数。</summary>
    sealed class Stats
    {
        public int Han;
        public int Kana;
        public int Hangul;
        public int Thai;
        public int LatinLetters;
        public int CyrillicLetters;
        public int CyrillicWords;
        public readonly List<string> LatinWords = new();
    }

    static Stats Measure(string text)
    {
        var stats = new Stats();
        var word = new System.Text.StringBuilder();
        var wordScript = Script.Latin;

        void Flush()
        {
            if (word.Length == 0)
                return;
            if (wordScript == Script.Latin)
                stats.LatinWords.Add(word.ToString().ToLowerInvariant());
            else
                stats.CyrillicWords++;
            word.Clear();
        }

        foreach (var ch in text)
        {
            if (IsHan(ch))
            {
                Flush();
                stats.Han++;
            }
            else if (ch is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ' or >= 'ｦ' and <= 'ﾟ')
            {
                Flush();
                stats.Kana++;
            }
            else if (ch is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏')
            {
                Flush();
                stats.Hangul++;
            }
            else if (ch is >= '฀' and <= '๿')
            {
                Flush();
                stats.Thai++;
            }
            else if (ch is >= 'Ѐ' and <= 'ӿ')
            {
                if (word.Length > 0 && wordScript != Script.Cyrillic)
                    Flush();
                wordScript = Script.Cyrillic;
                word.Append(ch);
                stats.CyrillicLetters++;
            }
            else if (IsLatinLetter(ch) || (ch is '\'' or '’' && word.Length > 0 && wordScript == Script.Latin))
            {
                if (word.Length > 0 && wordScript != Script.Latin)
                    Flush();
                wordScript = Script.Latin;
                word.Append(ch == '’' ? '\'' : ch);
                if (ch is not ('\'' or '’'))
                    stats.LatinLetters++;
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark && word.Length > 0)
            {
                word.Append(ch);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return stats;
    }

    static bool IsHan(char ch) => ch is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿';

    static bool IsLatinLetter(char ch) =>
        ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || (ch is >= 'À' and <= 'ɏ' or >= 'Ḁ' and <= 'ỿ' && char.IsLetter(ch));

    /// <summary>这段文字有多像 <paramref name="language"/>（另一种语言是 <paramref name="other"/>）。
    /// 汉字一个字算一份，英文一个词算一份，所以「帮我 fix 这个 bug」算中文。</summary>
    static int Score(Stats stats, Language language, Language other) => language.Script switch
    {
        // 中日两种语言放在一起时，有假名的才算日文。
        Script.Han => other.Script == Script.Japanese && stats.Kana > 0 ? 0 : stats.Han,
        Script.Japanese => stats.Kana + (stats.Kana > 0 || other.Script != Script.Han ? stats.Han : 0),
        Script.Hangul => stats.Hangul,
        Script.Thai => stats.Thai,
        Script.Cyrillic => stats.CyrillicWords,
        _ => other.Script == Script.Latin ? stats.LatinWords.Count(word => language.Common.Contains(word)) : stats.LatinWords.Count,
    };

    /// <summary>这种语言的字够不够翻：汉字、假名这类至少一个，字母至少三个（「ok」「v2」不翻）。</summary>
    static bool Enough(Stats stats, Language language) => language.Script switch
    {
        Script.Han => stats.Han > 0,
        Script.Japanese => stats.Kana + stats.Han > 0,
        Script.Hangul => stats.Hangul > 0,
        Script.Thai => stats.Thai > 0,
        Script.Cyrillic => stats.CyrillicLetters >= 3,
        _ => stats.LatinLetters >= 3,
    };

    /// <summary>自动方向时：这段更像在学的语言（应该翻回母语）。拿不准就当成母语。</summary>
    public static bool LooksLikeTarget(string text, Language native, Language target)
    {
        var stats = Measure(text);
        return Score(stats, target, native) > Score(stats, native, target);
    }

    public static bool HasEnough(string text, Language language) => Enough(Measure(text), language);

    /// <summary>忽略大小写、空白和标点后是不是同一句，用来判断「更地道的说法」和原句有没有区别。</summary>
    public static bool SameSentence(string a, string b)
    {
        static string Squash(string text) =>
            new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Squash(a) == Squash(b);
    }
}
