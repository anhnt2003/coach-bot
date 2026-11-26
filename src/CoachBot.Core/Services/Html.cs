using System.Net;
using System.Text.RegularExpressions;

namespace CoachBot.Core.Services;

/// <summary>
/// Markup in Telegram's HTML subset (&lt;b&gt;, &lt;i&gt;, &lt;s&gt;, &lt;blockquote expandable&gt;…), which is what
/// <see cref="Abstractions.OutboundMessage.Text"/> holds. A plain string is text; wrapping it in <see cref="Html"/> says
/// it is already markup. <see cref="MessageFormatter.T"/> escapes plain string arguments and keeps <see cref="Html"/>
/// ones, so text from the LLM, the athlete or an error can never break a message.
/// </summary>
public readonly partial record struct Html(string Value)
{
    public static readonly Html Empty = new("");

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public static Html Text(string? text) => new(Encode(text));

    public static string Encode(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public static Html Bold(Html inner) => new($"<b>{inner.Value}</b>");

    public static Html Bold(string text) => Bold(Text(text));

    public static Html Italic(Html inner) => new($"<i>{inner.Value}</i>");

    public static Html Strike(Html inner) => new($"<s>{inner.Value}</s>");

    /// <summary>A quote Telegram shows collapsed to its first lines, with a tap to expand.</summary>
    public static Html Expandable(Html inner) => new($"<blockquote expandable>{inner.Value}</blockquote>");

    /// <summary>Lines joined with newlines, skipping empty ones.</summary>
    public static Html Lines(params Html[] lines) => Lines((IEnumerable<Html>)lines);

    public static Html Lines(IEnumerable<Html> lines) => new(string.Join("\n", lines.Where(l => !l.IsEmpty).Select(l => l.Value)));

    /// <summary>Blocks separated by a blank line, skipping empty ones.</summary>
    public static Html Blocks(params Html[] blocks) => Blocks((IEnumerable<Html>)blocks);

    public static Html Blocks(IEnumerable<Html> blocks) => new(string.Join("\n\n", blocks.Where(b => !b.IsEmpty).Select(b => b.Value.Trim('\n'))));

    /// <summary>"• item" lines for plain-text items, skipping blank ones.</summary>
    public static Html Bullets(IEnumerable<string>? items) =>
        Lines((items ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => new Html("• " + Encode(i.Trim()))));

    /// <summary>The text without markup, for a client that cannot render it.</summary>
    public static string ToPlain(string html) => WebUtility.HtmlDecode(TagPattern().Replace(html, ""));

    public static implicit operator string(Html html) => html.Value ?? "";

    public override string ToString() => Value ?? "";

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagPattern();
}
