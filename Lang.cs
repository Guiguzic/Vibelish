namespace Vibelish;

/// <summary>判断一段文字里有没有中文、英文，决定往哪个方向翻。</summary>
static class Lang
{
    public static bool HasCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= (char)0x4E00 and <= (char)0x9FFF)
                return true;
            if (ch is >= (char)0x3400 and <= (char)0x4DBF)
                return true;
            if (ch is >= (char)0xF900 and <= (char)0xFAFF)
                return true;
        }

        return false;
    }

    /// <summary>至少有三个英文字母才算一句英文，「ok」「v2」这种不翻。</summary>
    public static bool HasEnglish(string text)
    {
        var letters = 0;
        foreach (var ch in text)
        {
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') && ++letters >= 3)
                return true;
        }

        return false;
    }

    /// <summary>忽略大小写、空白和标点后是不是同一句，用来判断「更地道的说法」和原句有没有区别。</summary>
    public static bool SameSentence(string a, string b)
    {
        static string Squash(string text) =>
            new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Squash(a) == Squash(b);
    }
}
