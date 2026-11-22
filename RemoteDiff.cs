using System.Text;
using System.Text.RegularExpressions;

namespace Vibelish;

/// <summary>
/// 远程窗口（Docker / WSL 的 Linux 程序）只能整窗 OCR。开始听写时先记下窗口里原有的每一行，
/// 之后新冒出来的行就是正在打的字；取最靠下的一组，输入框一般在最下面。
/// </summary>
static class RemoteDiff
{
    static readonly Regex CandidateBar = new(
        @"(?:^|\s)[1-9]\s*[\.．、:]?\s*\S+.*\s[2-9]\s*[\.．、:]?\s*\S",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Pick(IReadOnlyList<OcrReader.OcrLine> baseline, IReadOnlyList<OcrReader.OcrLine> current)
    {
        var known = new HashSet<string>(baseline.Select(line => Key(line.Text)));
        var fresh = new List<OcrReader.OcrLine>();
        foreach (var line in current)
        {
            var key = Key(line.Text);
            if (key.Length == 0 || known.Contains(key))
                continue;
            if (CandidateBar.IsMatch(line.Text) || OnlyTicked(line, baseline))
                continue;
            fresh.Add(line);
        }

        if (fresh.Count == 0)
            return "";

        fresh.Sort((a, b) => a.Box.Y.CompareTo(b.Box.Y));
        var groups = new List<List<OcrReader.OcrLine>> { new() { fresh[0] } };
        for (var i = 1; i < fresh.Count; i++)
        {
            var last = groups[^1][^1];
            var gap = fresh[i].Box.Top - last.Box.Bottom;
            var lineHeight = Math.Max(12, Math.Max(last.Box.Height, fresh[i].Box.Height));
            if (gap <= lineHeight * 1.3 && Overlaps(groups[^1], fresh[i]))
                groups[^1].Add(fresh[i]);
            else
                groups.Add(new() { fresh[i] });
        }

        var chosen = groups.OrderByDescending(group => group.Max(line => line.Box.Bottom)).First();
        if (chosen.Count > 6)
            chosen = chosen.Skip(chosen.Count - 6).ToList();

        var sb = new StringBuilder();
        foreach (var line in chosen)
        {
            var text = line.Text.Trim();
            if (sb.Length > 0 && !(IsWide(sb[^1]) && IsWide(text[0])))
                sb.Append(' ');
            sb.Append(text);
        }

        return sb.ToString().Trim();
    }

    /// <summary>同一位置原来就有一行，只是数字变了（计时、计数），不算新打的字。</summary>
    static bool OnlyTicked(OcrReader.OcrLine line, IReadOnlyList<OcrReader.OcrLine> baseline)
    {
        var shape = Digitless(line.Text);
        foreach (var old in baseline)
        {
            if (Math.Abs(old.Box.Y - line.Box.Y) > Math.Max(6, line.Box.Height / 2)
                || Math.Abs(old.Box.X - line.Box.X) > 24)
                continue;
            if (Digitless(old.Text) == shape)
                return true;
        }

        return false;
    }

    static bool Overlaps(List<OcrReader.OcrLine> group, OcrReader.OcrLine line)
    {
        var left = group.Min(item => item.Box.Left);
        var right = group.Max(item => item.Box.Right);
        return line.Box.Left < right + 40 && line.Box.Right > left - 40;
    }

    static string Key(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    static string Digitless(string text) => new(Key(text).Where(ch => !char.IsDigit(ch)).ToArray());

    static bool IsWide(char ch) => ch is >= '⺀' and <= '鿿' or >= '＀' and <= '￯' or >= '　' and <= '〿';

    public static string? SelfTest()
    {
        static OcrReader.OcrLine L(string text, int x, int y, int w = 300) => new(text, new Rectangle(x, y, w, 15));
        var baseline = new[]
        {
            L("The renders are still running.", 590, 815),
            L("输入/以使用命令", 617, 962, 118),
            L("12分钟", 1835, 954, 41),
        };

        var cases = new (string Name, OcrReader.OcrLine[] Now, string Expect)[]
        {
            ("nothing", [baseline[0], baseline[1], baseline[2]], ""),
            ("typed", [baseline[0], L("我想把这个板子再检查一遍", 617, 962), L("12分钟", 1835, 954, 41)], "我想把这个板子再检查一遍"),
            ("timer", [baseline[0], L("我想检查", 617, 962), L("13分钟", 1835, 954, 41)], "我想检查"),
            ("wrap", [baseline[0], L("第一行写到这里还没完", 617, 940), L("接着第二行", 617, 962)], "第一行写到这里还没完接着第二行"),
            ("bottom-wins", [L("上面新出来的回复", 590, 500), L("我在打字", 617, 962)], "我在打字"),
            ("candidates", [L("1.你好 2.拟好 3.妮好", 640, 990), L("nihao", 617, 962)], "nihao"),
            ("english", [L("I want to check", 617, 962), L("the board again", 617, 980)], "I want to check the board again"),
        };

        foreach (var item in cases)
        {
            var actual = Pick(baseline, item.Now);
            if (!string.Equals(actual, item.Expect, StringComparison.Ordinal))
                return "remote " + item.Name + " => [" + actual + "] expect [" + item.Expect + "]";
        }

        return null;
    }
}
