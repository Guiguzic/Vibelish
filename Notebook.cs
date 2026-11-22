using System.Text;
using System.Text.Json;

namespace Vibelish;

/// <summary>学习记录里的一条：自己写的一句外语（带纠错），或者一句母语和它的译文。</summary>
sealed class NoteEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>write：自己用在学的语言写的；translate：写母语，翻成在学的语言。</summary>
    public string Kind { get; set; } = "write";

    public string Native { get; set; } = "zh-CN";

    public string Target { get; set; } = "en";

    /// <summary>自己打的那句。</summary>
    public string Source { get; set; } = "";

    /// <summary>卡片上的译文：write 是母语意思，translate 是外语译文。</summary>
    public string Result { get; set; } = "";

    /// <summary>write 时更地道的说法；空字符串表示原句已经很好。</summary>
    public string Better { get; set; } = "";

    public List<CoachFix> Fixes { get; set; } = new();

    public string Habit { get; set; } = "";

    public string Tip { get; set; } = "";

    /// <summary>复习用的盒子（0–5），记住一次往上一格，忘了回到 0。</summary>
    public int Box { get; set; }

    /// <summary>下次该复习的时间。</summary>
    public DateTime Due { get; set; } = DateTime.Now;

    public int Reviews { get; set; }

    public int Lapses { get; set; }

    /// <summary>复习时要不要出现：有错误的句子，或者中译英。写得全对的句子只算进统计。</summary>
    public bool Reviewable => Kind == "translate" || Fixes.Count > 0;

    public bool Mastered => Box >= Notebook.Intervals.Length;
}

sealed class NotebookData
{
    public List<NoteEntry> Entries { get; set; } = new();

    public string Report { get; set; } = "";

    public DateTime? ReportTime { get; set; }
}

/// <param name="Written">今天自己写了几句外语。</param>
/// <param name="Fixed">今天被纠正了几处。</param>
/// <param name="Translated">今天写母语翻成外语的句数。</param>
/// <param name="Streak">连续学习了几天（今天还没写也算上昨天为止）。</param>
/// <param name="Due">现在该复习的条数。</param>
/// <param name="TotalWritten">一共写了几句外语。</param>
/// <param name="TotalFixes">一共被纠正了几处。</param>
/// <param name="CleanRate">最近 30 天写的句子里，一处错误都没有的比例（0–100）；没写过是 -1。</param>
sealed record NotebookStats(int Written, int Fixed, int Translated, int Streak, int Due, int TotalWritten, int TotalFixes, int CleanRate);

/// <summary>
/// 学习记录（错题本 + 中译英），存在 %APPDATA%\Vibelish\notebook.json。只在 UI 线程上用。
/// 复习按间隔重复：记住了隔 1、2、4、7、15、30 天再出现，忘了从头来。
/// </summary>
sealed class Notebook
{
    public const int MaxEntries = 3000;

    /// <summary>第 n 格记住以后，隔几天再复习。走完最后一格算掌握。</summary>
    public static readonly int[] Intervals = [1, 2, 4, 7, 15, 30];

    static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    readonly string _path;
    NotebookData _data = new();

    Notebook(string path)
    {
        _path = path;
    }

    public static string DefaultPath => Path.Combine(AppConfig.Directory, "notebook.json");

    public IReadOnlyList<NoteEntry> Entries => _data.Entries;

    public string Report => _data.Report;

    public DateTime? ReportTime => _data.ReportTime;

    /// <summary>有新记录或者复习过了（学习记录窗口要刷新）。</summary>
    public event EventHandler? Changed;

    public static Notebook Load(string? path = null)
    {
        var notebook = new Notebook(path ?? DefaultPath);
        try
        {
            if (File.Exists(notebook._path))
            {
                notebook._data = JsonSerializer.Deserialize<NotebookData>(File.ReadAllText(notebook._path)) ?? new NotebookData();
                notebook._data.Entries ??= new List<NoteEntry>();
                foreach (var entry in notebook._data.Entries)
                    entry.Fixes ??= new List<CoachFix>();
            }
        }
        catch (Exception ex)
        {
            // 文件坏了也要能用：留一份副本，从空的开始，免得下次保存把它盖掉。
            TraceLog.Note("notebook unreadable " + ex.GetType().Name);
            try
            {
                File.Copy(notebook._path, notebook._path + ".bad", true);
            }
            catch
            {
                // Ignore.
            }
        }

        return notebook;
    }

    /// <summary>写完的一句（回车发出去或 Esc 完成）。同一句话（比如改了一个字又完成一次）只留最新的。</summary>
    public NoteEntry? Add(string source, TranslateResult result, AppConfig config)
    {
        source = source.Trim();
        if (source.Length < 2 || result.Text.Trim().Length == 0 || result.Coach is not { } coach)
            return null;

        var entry = new NoteEntry
        {
            Kind = result.ToNative ? "write" : "translate",
            Native = config.NativeLanguage,
            Target = config.TargetLanguage,
            Source = source,
            Result = result.Text.Trim(),
            Better = result.ToNative ? result.Better ?? "" : "",
            Fixes = coach.Fixes.ToList(),
            Habit = coach.Habit,
            Tip = coach.Tip,
        };
        // 当天第一次见到就该能复习；明天再出现一次效果最好，所以今天的放在队尾。
        entry.Due = entry.Time;

        _data.Entries.RemoveAll(old => old.Time.Date == entry.Time.Date && old.Kind == entry.Kind
            && string.Equals(old.Source, entry.Source, StringComparison.OrdinalIgnoreCase));
        _data.Entries.Add(entry);
        if (_data.Entries.Count > MaxEntries)
            _data.Entries.RemoveRange(0, _data.Entries.Count - MaxEntries);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    /// <summary>复习的结果：记住了往上一格，忘了回到第 0 格、明天再来。</summary>
    public void Grade(NoteEntry entry, bool remembered, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        entry.Reviews++;
        if (remembered)
        {
            entry.Box++;
            entry.Due = entry.Mastered ? DateTime.MaxValue.AddYears(-1) : at.Date.AddDays(Intervals[Math.Min(entry.Box, Intervals.Length) - 1]).AddHours(4);
        }
        else
        {
            entry.Box = 0;
            entry.Lapses++;
            entry.Due = at.Date.AddDays(1).AddHours(4);
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(NoteEntry entry)
    {
        if (!_data.Entries.Remove(entry))
            return;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetReport(string report)
    {
        _data.Report = report;
        _data.ReportTime = DateTime.Now;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>现在该复习的，错题在前，越早该复习的越靠前。</summary>
    public List<NoteEntry> DueEntries(DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        return _data.Entries
            .Where(entry => entry.Reviewable && !entry.Mastered && entry.Due <= at)
            .OrderBy(entry => entry.Kind == "write" ? 0 : 1)
            .ThenBy(entry => entry.Due)
            .ToList();
    }

    public NotebookStats Stats(DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        var today = _data.Entries.Where(entry => entry.Time.Date == at.Date).ToList();
        var days = new HashSet<DateTime>(_data.Entries.Select(entry => entry.Time.Date));
        var streak = 0;
        var day = days.Contains(at.Date) ? at.Date : at.Date.AddDays(-1);
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }

        var written = _data.Entries.Where(entry => entry.Kind == "write").ToList();
        var recent = written.Where(entry => entry.Time >= at.AddDays(-30)).ToList();
        return new NotebookStats(
            today.Count(entry => entry.Kind == "write"),
            today.Where(entry => entry.Kind == "write").Sum(entry => entry.Fixes.Count),
            today.Count(entry => entry.Kind == "translate"),
            streak,
            DueEntries(at).Count,
            written.Count,
            written.Sum(entry => entry.Fixes.Count),
            recent.Count == 0 ? -1 : (int)Math.Round(100.0 * recent.Count(entry => entry.Fixes.Count == 0) / recent.Count));
    }

    /// <summary>最近 30 天每类错误出现几次，多的在前；带一个最近的例子。</summary>
    public List<(string Type, int Count, CoachFix Example)> TopMistakes(DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        return _data.Entries
            .Where(entry => entry.Kind == "write" && entry.Time >= at.AddDays(-30))
            .OrderByDescending(entry => entry.Time)
            .SelectMany(entry => entry.Fixes)
            .GroupBy(fix => fix.Type)
            .Select(group => (group.Key, group.Count(), group.First()))
            .OrderByDescending(item => item.Item2)
            .ToList();
    }

    /// <summary>交给大模型做诊断的材料：最近的错误在前，再加几句中译英。控制在几千字以内。</summary>
    public string DiagnosisMaterial(int maxSentences = 40)
    {
        var text = new StringBuilder();
        var written = _data.Entries.Where(entry => entry.Kind == "write").OrderByDescending(entry => entry.Time).Take(maxSentences).ToList();
        text.Append("Sentences the learner wrote (").Append(written.Count).Append("), with the mistakes found:\n");
        foreach (var entry in written)
        {
            text.Append("- ").Append(Clip(entry.Source, 240)).Append('\n');
            foreach (var fix in entry.Fixes)
                text.Append("  * [").Append(fix.Type).Append("] ").Append(fix.Wrong).Append(" -> ").Append(fix.Right)
                    .Append(fix.Why.Length > 0 ? " (" + fix.Why + ")" : "").Append('\n');
            if (entry.Fixes.Count == 0)
                text.Append("  * no mistakes\n");
        }

        var translated = _data.Entries.Where(entry => entry.Kind == "translate").OrderByDescending(entry => entry.Time).Take(10).ToList();
        if (translated.Count > 0)
        {
            text.Append("\nSentences the learner wrote in their native language and the translations they were shown:\n");
            foreach (var entry in translated)
                text.Append("- ").Append(Clip(entry.Source, 160)).Append(" => ").Append(Clip(entry.Result, 240)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>把纠错逐处替换进原句，得到「只改错、不润色」的版本。找不到的那处就跳过。</summary>
    public static string ApplyFixes(string source, IEnumerable<CoachFix> fixes)
    {
        var text = source;
        foreach (var fix in fixes)
        {
            if (fix.Wrong.Length == 0)
                continue;
            var at = text.IndexOf(fix.Wrong, StringComparison.Ordinal);
            if (at < 0)
                at = text.IndexOf(fix.Wrong, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
                text = text[..at] + fix.Right + text[(at + fix.Wrong.Length)..];
        }

        return text;
    }

    /// <summary>做诊断至少要写过几句（太少说不出规律）。</summary>
    public int WrittenCount => _data.Entries.Count(entry => entry.Kind == "write");

    static string Clip(string text, int max)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>写到临时文件再替换。</summary>
    void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, Options), new UTF8Encoding(false));
            File.Move(temp, _path, true);
        }
        catch (Exception ex)
        {
            TraceLog.Note("notebook save fail " + TraceLog.Safe(ex.Message));
        }
    }

    /// <summary>用临时文件跑一遍：记录、去重、统计、复习排期。</summary>
    public static string? SelfTest()
    {
        var path = Path.Combine(Path.GetTempPath(), "vibelish-notebook-test-" + Environment.ProcessId + ".json");
        try
        {
            File.Delete(path);
            var config = new AppConfig().Normalize();
            var book = Load(path);
            var fix = new CoachFix("go", "went", "grammar", "yesterday 用过去时");
            var wrong = new TranslateResult(true, "我昨天去了图书馆。", "test", ToNative: true, Better: "I went to the library yesterday.",
                Coach: new Coaching(new[] { fix }, "中文靠时间词，英文靠动词变形", ""));
            if (book.Add("I go to library yesterday.", wrong, config) == null)
                return "notebook add";
            book.Add("I go to library yesterday.", wrong, config);
            book.Add("I went to the library.", new TranslateResult(true, "我去了图书馆。", "test", ToNative: true, Better: "", Coach: new Coaching(Array.Empty<CoachFix>(), "", "")), config);
            book.Add("政策已经落实了。", new TranslateResult(true, "The policy has been implemented.", "test", Coach: new Coaching(Array.Empty<CoachFix>(), "", "用被动")), config);
            if (book.Add("没有点拨", new TranslateResult(true, "No coach.", "test"), config) != null)
                return "notebook needs coach";
            if (book.Entries.Count != 3)
                return "notebook dedupe " + book.Entries.Count;

            var stats = book.Stats();
            if (stats.Written != 2 || stats.Fixed != 1 || stats.Translated != 1 || stats.Streak != 1 || stats.Due != 2 || stats.CleanRate != 50)
                return "notebook stats " + stats;
            if (book.TopMistakes() is not [("grammar", 1, _)])
                return "notebook top";
            if (!book.DiagnosisMaterial().Contains("go -> went", StringComparison.Ordinal))
                return "notebook material";
            if (ApplyFixes("I go to library yesterday.", [fix, new CoachFix("to library", "to the library", "grammar", ""), new CoachFix("zzz", "y", "word", "")])
                != "I went to the library yesterday.")
                return "notebook apply fixes";

            var first = book.DueEntries()[0];
            if (first.Kind != "write")
                return "notebook due order";
            book.Grade(first, remembered: true);
            if (first.Box != 1 || first.Due.Date != DateTime.Today.AddDays(1) || book.Stats().Due != 1)
                return "notebook grade up";
            book.Grade(first, remembered: false);
            if (first.Box != 0 || first.Lapses != 1)
                return "notebook grade down";
            for (var i = 0; i < Intervals.Length; i++)
                book.Grade(first, remembered: true);
            if (!first.Mastered || book.DueEntries(DateTime.Now.AddYears(5)).Contains(first))
                return "notebook mastered";

            book.SetReport("诊断");
            var again = Load(path);
            if (again.Entries.Count != 3 || again.Report != "诊断" || again.Entries[0].Fixes.Count != 1 || again.Entries[0].Fixes[0].Right != "went")
                return "notebook reload";
            return null;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ignore.
            }
        }
    }
}
