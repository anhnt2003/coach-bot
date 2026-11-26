using System.Text.RegularExpressions;

namespace CoachBot.Infrastructure.Telegram;

/// <summary>Cuts Telegram HTML into messages under Telegram's length limit without breaking its markup.</summary>
public static partial class TelegramHtml
{
    /// <summary>
    /// Chunks of at most <paramref name="max"/> characters, cut at line ends and preferably at a blank line outside any
    /// tag. A tag open at a cut (e.g. a long expandable quote) is closed there and reopened at the start of the next chunk.
    /// </summary>
    public static List<string> Split(string html, int max)
    {
        var chunks = new List<string>();
        var lines = new List<string>();
        var startTags = new List<(string Name, string Tag)>(); // open when the current chunk starts
        var tags = new List<(string Name, string Tag)>(); // open after the current chunk's lines

        int Length(int count) =>
            Opening(startTags).Length + lines.Take(count).Sum(l => l.Length + 1);

        void Emit(int count)
        {
            var stack = new List<(string Name, string Tag)>(startTags);
            foreach (var line in lines.Take(count))
            {
                Track(line, stack);
            }

            var text = (Opening(startTags) + string.Join("\n", lines.Take(count))).Trim('\n') + Closing(stack);
            chunks.Add(text);
            lines.RemoveRange(0, count);
            while (lines.Count > 0 && lines[0].Length == 0)
            {
                lines.RemoveAt(0);
            }

            startTags = stack;
        }

        foreach (var piece in html.Split('\n').SelectMany(l => Pieces(l, max / 2)))
        {
            var after = new List<(string Name, string Tag)>(tags);
            Track(piece, after);
            if (lines.Count > 0 && Length(lines.Count) + piece.Length + Closing(after).Length > max)
            {
                Emit(LastBlankCut() is int cut and > 0 ? cut : lines.Count);
                if (lines.Count > 0 && Length(lines.Count) + piece.Length + Closing(after).Length > max)
                {
                    Emit(lines.Count);
                }
            }

            lines.Add(piece);
            tags = after;
        }

        if (lines.Count > 0)
        {
            Emit(lines.Count);
        }

        return chunks.Where(c => c.Length > 0).DefaultIfEmpty("").ToList();

        // Number of lines before the last blank line that is outside every tag.
        int? LastBlankCut()
        {
            var stack = new List<(string Name, string Tag)>(startTags);
            int? cut = null;
            for (var i = 0; i < lines.Count; i++)
            {
                Track(lines[i], stack);
                if (lines[i].Length == 0 && stack.Count == 0)
                {
                    cut = i;
                }
            }

            return cut;
        }
    }

    /// <summary>A line longer than <paramref name="max"/>, cut at spaces outside tags and entities.</summary>
    private static IEnumerable<string> Pieces(string line, int max)
    {
        while (line.Length > max)
        {
            var cut = max;
            while (cut > max / 2 && !(line[cut] == ' ' && line.LastIndexOf('<', cut) <= line.LastIndexOf('>', cut)
                                      && line.LastIndexOf('&', cut) <= line.LastIndexOf(';', cut)))
            {
                cut--;
            }

            yield return line[..cut];
            line = line[cut..].TrimStart();
        }

        yield return line;
    }

    private static void Track(string text, List<(string Name, string Tag)> open)
    {
        foreach (Match m in TagPattern().Matches(text))
        {
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (m.Groups[1].Value != "/")
            {
                open.Add((name, m.Value));
            }
            else if (open.FindLastIndex(t => t.Name == name) is var i and >= 0)
            {
                open.RemoveAt(i);
            }
        }
    }

    private static readonly HashSet<string> AllowedTags = ["b", "strong", "i", "em", "u", "ins", "s", "strike", "del", "code", "pre", "a", "blockquote", "tg-spoiler"];

    /// <summary>
    /// Why Telegram would reject this HTML: an unsupported or unbalanced tag, or a bare &lt;, &gt; or &amp; in the text.
    /// Empty when it is valid. Used by tests to check every message and template.
    /// </summary>
    public static IReadOnlyList<string> Problems(string html)
    {
        var problems = new List<string>();
        var open = new Stack<string>();
        var position = 0;
        foreach (Match m in TagPattern().Matches(html))
        {
            CheckText(html[position..m.Index]);
            position = m.Index + m.Length;
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (!AllowedTags.Contains(name))
            {
                problems.Add($"unsupported tag {m.Value}");
            }
            else if (m.Groups[1].Value != "/")
            {
                open.Push(name);
            }
            else if (open.Count == 0 || open.Pop() != name)
            {
                problems.Add($"unbalanced {m.Value}");
            }
        }

        CheckText(html[position..]);
        problems.AddRange(open.Select(t => $"unclosed <{t}>"));
        return problems;

        void CheckText(string text)
        {
            if (text.IndexOfAny(['<', '>']) >= 0)
            {
                problems.Add($"bare < or > in \"{Shorten(text)}\"");
            }

            if (BareAmpersand().IsMatch(text))
            {
                problems.Add($"bare & in \"{Shorten(text)}\"");
            }
        }

        static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;
    }

    private static string Opening(List<(string Name, string Tag)> open) => string.Concat(open.Select(t => t.Tag));

    private static string Closing(List<(string Name, string Tag)> open) => string.Concat(Enumerable.Reverse(open).Select(t => $"</{t.Name}>"));

    [GeneratedRegex("<(/?)([a-zA-Z-]+)[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex("&(?!(lt|gt|amp|quot|#[0-9]+|#x[0-9a-fA-F]+);)")]
    private static partial Regex BareAmpersand();
}
