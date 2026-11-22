using System.Drawing.Drawing2D;

namespace Vibelish;

/// <summary>
/// 卡片上的纠错与点拨。自己写外语时一处一行：类型标签、写错的（划掉）→ 改正的，下面一句为什么；
/// 最后是这些错误背后的母语思维。写母语时只有一句写法点拨。
/// 开了句子拆解，再把外语句子按主语、谓语、宾语……一块一块列出来，每块一句大白话。
/// </summary>
sealed class CoachPanel : Control
{
    readonly record struct Chip(Rectangle Bounds, string Text, Color Color);

    readonly record struct Run(Rectangle Bounds, string Text, Font Font, Color Color, bool Wrap);

    readonly List<Chip> _chips = new();
    readonly List<Run> _runs = new();
    Coaching? _coach;
    bool _reverse;
    Language _native = Languages.Find("zh-CN");
    Language _target = Languages.Find("en");
    bool _beginner = true;

    public CoachPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        TabStop = false;
        Visible = false;
    }

    /// <summary>有东西可显示：反向纠错时总要说一句（没错误也说），正向时要有点拨；有拆解也算。</summary>
    public bool HasContent => _coach != null && ((_reverse && _coach.Checked) || _coach.Tip.Length > 0 || _coach.PartList.Count > 0 || _coach.Order.Length > 0);

    /// <summary>反向时一处错误、一句思维提示、一块拆解都没有。</summary>
    public bool Clean => _coach is { Fixes.Count: 0, Habit.Length: 0 } && _coach.PartList.Count == 0;

    public void SetLanguages(Language native, Language target, bool beginner = true)
    {
        _native = native;
        _target = target;
        _beginner = beginner;
    }

    static Color RoleColor(string role, Palette p) => SentencePart.ColorFor(role, p.Dark);

    public void Show(Coaching? coach, bool reverse)
    {
        _coach = coach;
        _reverse = reverse;
        Invalidate();
    }

    public void Clear()
    {
        _coach = null;
        _chips.Clear();
        _runs.Clear();
        Visible = false;
    }

    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    static Size Measure(string text, Font font, int width, bool wrap) => TextRenderer.MeasureText(
        text.Length == 0 ? " " : text,
        font,
        new Size(Math.Max(10, width), int.MaxValue),
        Flags | (wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine));

    static Color TypeColor(string type, Palette p) => type switch
    {
        "grammar" => p.Error,
        "literal" => p.Warning,
        "spelling" or "punctuation" => p.Muted,
        _ => p.Accent,
    };

    /// <summary>按宽度排好，返回高度。颜色也在这里取，换主题后重新排一次就行。</summary>
    public int Arrange(int width, Func<int, int> scale)
    {
        _chips.Clear();
        _runs.Clear();
        if (_coach is not { } coach)
            return 0;

        var p = Theme.Current;
        var chipFont = Theme.Card(7.5F);
        var captionFont = Theme.Card(8.25F);
        var noteFont = Theme.CardText(_native.Font, 8.75F);
        var wrongFont = Theme.CardText(_target.Font, 10F, FontStyle.Strikeout);
        var rightFont = Theme.CardText(_target.Font, 10F, FontStyle.Bold);
        var arrow = " → ";
        var chipPad = scale(6);
        var gap = scale(8);
        var chipHeight = chipFont.Height + scale(3);

        // 标签一样宽，后面的文字才对得齐。
        var labels = coach.Fixes.Select(fix => Coaching.Label(fix.Type, _native, _target)).ToList();
        var extra = _reverse ? "思维" : "点拨";
        var parts = coach.PartList;
        var roleLabels = parts.Select(part => SentencePart.Label(part.Role, _native)).ToList();
        var chipWidth = labels.Append(extra).Append("骨架").Append("语序").Concat(roleLabels).Max(label => Measure(label, chipFont, 200, false).Width) + chipPad * 2;
        var partFont = Theme.CardText(_target.Font, 10F, FontStyle.Bold);
        var x0 = chipWidth + gap;
        var avail = Math.Max(scale(60), width - x0);
        var y = 0;

        void AddChip(string text, Color color, int top, int lineHeight) =>
            _chips.Add(new Chip(new Rectangle(0, top + Math.Max(0, (lineHeight - chipHeight) / 2), chipWidth, chipHeight), text, color));

        int AddText(string text, Font font, Color color, int x, int top, int room)
        {
            var size = Measure(text, font, room, true);
            _runs.Add(new Run(new Rectangle(x, top, Math.Min(room, size.Width + 2), size.Height), text, font, color, true));
            return size.Height;
        }

        if (_reverse && coach.Checked)
        {
            var caption = coach.Fixes.Count > 0 ? "纠错 · " + coach.Fixes.Count + " 处" : "语法、用词都没发现错误";
            y += AddText(caption, captionFont, coach.Fixes.Count > 0 ? p.Dim : p.Success, 0, y, width) + scale(6);
        }

        for (var i = 0; i < coach.Fixes.Count; i++)
        {
            var fix = coach.Fixes[i];
            var top = y;
            var lineHeight = Math.Max(wrongFont.Height, rightFont.Height);
            AddChip(labels[i], TypeColor(fix.Type, p), top, lineHeight);
            var wrong = fix.Wrong.Length > 0 ? fix.Wrong : "（缺少）";
            var wrongSize = Measure(wrong, wrongFont, avail, true);
            var arrowWidth = Measure(arrow, rightFont, 200, false).Width;
            var rightSize = Measure(fix.Right, rightFont, avail - arrowWidth, true);
            var oneLine = wrongSize.Height <= lineHeight + 2 && rightSize.Height <= lineHeight + 2
                && wrongSize.Width + arrowWidth + rightSize.Width <= avail;
            _runs.Add(new Run(new Rectangle(x0, y, Math.Min(avail, wrongSize.Width + 2), wrongSize.Height), wrong, wrongFont, fix.Wrong.Length > 0 ? p.Error : p.Dim, true));
            var rightX = oneLine ? x0 + wrongSize.Width : x0;
            var rightY = oneLine ? y : y + wrongSize.Height + scale(1);
            _runs.Add(new Run(new Rectangle(rightX, rightY, arrowWidth, lineHeight), arrow, rightFont, p.Dim, false));
            var right = fix.Right.Length > 0 ? fix.Right : "（删掉）";
            _runs.Add(new Run(new Rectangle(rightX + arrowWidth, rightY, Math.Min(avail - (rightX - x0) - arrowWidth, rightSize.Width + 2), rightSize.Height),
                right, rightFont, p.Success, true));
            y = rightY + Math.Max(lineHeight, rightSize.Height);
            if (fix.Why.Length > 0)
                y += scale(2) + AddText(fix.Why, noteFont, p.Muted, x0, y + scale(2), avail);
            y += scale(9);
        }

        var note = _reverse ? coach.Habit : coach.Tip;
        if (note.Length > 0)
        {
            var noteHeight = Measure(note, noteFont, avail, true).Height;
            AddChip(extra, p.Accent, y, noteFont.Height);
            AddText(note, noteFont, p.Body, x0, y, avail);
            y += noteHeight + scale(9);
        }

        if (parts.Count > 0 || coach.Core.Length > 0 || coach.Order.Length > 0)
        {
            if (y > 0)
                y += scale(3);
            var caption = _beginner && _native.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "句子拆解 · 先看骨架，再看每块挂在谁身上"
                : "句子拆解";
            y += AddText(caption, captionFont, p.Dim, 0, y, width) + scale(6);

            // 骨架：只留主语、谓语、宾语，先抓住这句到底在说什么。
            if (coach.Core.Length > 0)
            {
                AddChip("骨架", p.Text, y, partFont.Height);
                y += AddText(coach.Core, partFont, p.Text, x0, y, avail) + scale(8);
            }

            // 语序：英文先说主干、修饰往后挂；中文把修饰堆在前面。
            if (coach.Order.Length > 0)
            {
                AddChip("语序", p.Accent, y, noteFont.Height);
                y += AddText(coach.Order, noteFont, p.Body, x0, y, avail) + scale(10);
            }

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var color = RoleColor(part.Role, p);
                AddChip(roleLabels[i], color, y, partFont.Height);
                y += AddText(part.Text, partFont, p.Text, x0, y, avail);
                // 先说这块挂在谁身上（修饰 speed、是 believe 的对象），没有就说这种成分一般干什么。
                var relation = SentencePart.Relation(part.Role, part.Of, _native);
                var plain = relation.Length > 0 ? relation : _beginner ? SentencePart.Plain(part.Role, _native) : "";
                var line = plain.Length > 0 && part.Note.Length > 0 ? plain + " · " + part.Note : plain.Length > 0 ? plain : part.Note;
                if (line.Length > 0)
                    y += scale(2) + AddText(line, noteFont, p.Muted, x0, y + scale(2), avail);
                y += scale(8);
            }
        }

        return Math.Max(0, y - scale(9));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Current.Card);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var chipFont = Theme.Card(7.5F);
        foreach (var chip in _chips)
        {
            using (var path = Theme.RoundedRect(chip.Bounds, chip.Bounds.Height / 2))
            using (var fill = new SolidBrush(Theme.Blend(Color.FromArgb(p.Dark ? 60 : 34, chip.Color), p.Card)))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, chip.Text, chipFont, chip.Bounds, chip.Color,
                Flags | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        foreach (var run in _runs)
        {
            TextRenderer.DrawText(g, run.Text, run.Font, run.Bounds, run.Color,
                Flags | (run.Wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine));
        }
    }
}
