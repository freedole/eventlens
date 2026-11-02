using System.Text;
using System.Text.RegularExpressions;

namespace EventLens.Core.Util;

public enum MdBlockKind { Heading, Paragraph, Bullet, Numbered, Code, Rule }

public sealed record MdInline(string Text, bool Bold = false, bool Italic = false, bool Code = false);

public sealed class MdBlock
{
    public required MdBlockKind Kind { get; init; }
    public int Level { get; init; }          // heading level or list indent
    public int Number { get; init; }         // for numbered items
    public List<MdInline> Inlines { get; init; } = [];
    public string Raw { get; init; } = "";   // for code blocks
}

/// <summary>Small Markdown subset parser for AI answers: headings, lists, bold/italic, inline and fenced code, rules.</summary>
public static partial class Markdown
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)[-*+•]\s+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(\s*)(\d+)[.)]\s+(.*)$")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|\*[^*\s][^*]*\*)")]
    private static partial Regex InlineRegex();

    public static List<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var para = new StringBuilder();

        void FlushPara()
        {
            if (para.Length == 0) return;
            blocks.Add(new MdBlock { Kind = MdBlockKind.Paragraph, Inlines = Inline(para.ToString().Trim()) });
            para.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```"))
            {
                FlushPara();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    code.AppendLine(lines[i]);
                    i++;
                }
                blocks.Add(new MdBlock { Kind = MdBlockKind.Code, Raw = code.ToString().TrimEnd() });
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { FlushPara(); continue; }

            if (RuleRegex().IsMatch(line)) { FlushPara(); blocks.Add(new MdBlock { Kind = MdBlockKind.Rule }); continue; }

            var h = HeadingRegex().Match(line);
            if (h.Success)
            {
                FlushPara();
                blocks.Add(new MdBlock { Kind = MdBlockKind.Heading, Level = h.Groups[1].Length, Inlines = Inline(h.Groups[2].Value.Trim().TrimEnd('#').Trim()) });
                continue;
            }

            var b = BulletRegex().Match(line);
            if (b.Success)
            {
                FlushPara();
                blocks.Add(new MdBlock { Kind = MdBlockKind.Bullet, Level = b.Groups[1].Value.Length / 2, Inlines = Inline(b.Groups[2].Value) });
                continue;
            }

            var n = NumberedRegex().Match(line);
            if (n.Success)
            {
                FlushPara();
                blocks.Add(new MdBlock
                {
                    Kind = MdBlockKind.Numbered,
                    Level = n.Groups[1].Value.Length / 2,
                    Number = int.TryParse(n.Groups[2].Value, out var num) ? num : 1,
                    Inlines = Inline(n.Groups[3].Value),
                });
                continue;
            }

            if (para.Length > 0) para.Append(' ');
            para.Append(line.Trim());
        }
        FlushPara();
        return blocks;
    }

    public static List<MdInline> Inline(string text)
    {
        var result = new List<MdInline>();
        int pos = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Index > pos) result.Add(new MdInline(text[pos..m.Index]));
            var v = m.Value;
            if (v.StartsWith('`')) result.Add(new MdInline(v[1..^1], Code: true));
            else if (v.StartsWith("**") || v.StartsWith("__")) result.Add(new MdInline(v[2..^2], Bold: true));
            else result.Add(new MdInline(v[1..^1], Italic: true));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) result.Add(new MdInline(text[pos..]));
        return result;
    }

    /// <summary>Plain text (for copying).</summary>
    public static string ToPlainText(string? markdown) =>
        string.Join(Environment.NewLine, Parse(markdown).Select(b => b.Kind switch
        {
            MdBlockKind.Code => b.Raw,
            MdBlockKind.Rule => "",
            MdBlockKind.Bullet => new string(' ', b.Level * 2) + "- " + string.Concat(b.Inlines.Select(i => i.Text)),
            MdBlockKind.Numbered => new string(' ', b.Level * 2) + b.Number + ". " + string.Concat(b.Inlines.Select(i => i.Text)),
            _ => string.Concat(b.Inlines.Select(i => i.Text)),
        }));
}
