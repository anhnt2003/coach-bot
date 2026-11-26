using CoachBot.Core.Domain;
using CoachBot.Core.Services;
using CoachBot.Infrastructure.Localization;
using CoachBot.Infrastructure.Telegram;

namespace CoachBot.Infrastructure.Tests;

public class TelegramHtmlTests
{
    private static readonly JsonTextCatalog Catalog = new(strict: true);

    /// <summary>Texts that are stored, pushed to COROS or used as button labels: never markup. "x." is a prefix, else a key.</summary>
    private static readonly string[] PlainPrefixes =
        ["workout.", "intensity.", "race.distance.", "plan.status.", "assess.level.", "assess.verdict.", "assess.limiter.", "button.", "symptom.button.", "work.", "job.name.", "llm.reason.", "common.yes", "common.no", "adapt."];

    [Fact]
    public void Every_template_is_valid_telegram_html_and_plain_texts_have_no_markup()
    {
        var problems = new List<string>();
        foreach (var lang in Catalog.Languages)
        {
            foreach (var key in Catalog.Keys(lang))
            {
                var template = Catalog.Get(lang, key);
                problems.AddRange(TelegramHtml.Problems(template).Select(p => $"{lang}/{key}: {p}"));
                if (PlainPrefixes.Any(p => p.EndsWith('.') ? key.StartsWith(p, StringComparison.Ordinal) : key == p) && template.IndexOfAny(['<', '&']) >= 0)
                {
                    problems.Add($"{lang}/{key}: plain text must not contain markup");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Template_arguments_are_escaped_unless_they_are_markup()
    {
        var fmt = new MessageFormatter(Catalog);

        var html = fmt.T("en", "daily.advice", "Keep HR < 150 & relax");
        Assert.Equal("💬 Keep HR &lt; 150 &amp; relax", html.Value);

        var nested = fmt.T("en", "llm.failed.plan", fmt.T("en", "readiness.level.green"));
        Assert.Contains("🟢 <b>Ready to train</b>", nested.Value, StringComparison.Ordinal);
        Assert.Empty(TelegramHtml.Problems(nested));
    }

    [Fact]
    public void Workout_lines_escape_titles_written_by_the_coach()
    {
        var fmt = new MessageFormatter(Catalog);
        var w = new PlannedWorkout
        {
            Date = new DateOnly(2026, 11, 17), Type = WorkoutType.Easy, TargetDistanceKm = 7, Title = "Easy <shakeout> & strides · 7 km",
            PaceMinSecPerKm = 430, PaceMaxSecPerKm = 480, HrMin = 140, HrMax = 158, Description = "Relax & breathe", Status = WorkoutStatus.Completed,
        };

        var line = fmt.Session("en", w).Value;

        Assert.Equal("<b>Tue 17/11</b> · Easy &lt;shakeout&gt; &amp; strides · 7 km ✅\n   7:10–8:00/km · HR 140–158\n<i>Relax &amp; breathe</i>", line);
        Assert.Equal("<b>T3 17/11</b>", fmt.Line("vi", w).Value[..15]);
    }

    [Fact]
    public void Plain_fallback_drops_markup_and_decodes_entities()
    {
        Assert.Equal("Ready · HR < 150 & easy", Html.ToPlain("<b>Ready</b> · HR &lt; 150 &amp; <i>easy</i>"));
    }

    [Fact]
    public void A_short_message_is_one_chunk()
    {
        Assert.Equal(["<b>Hi</b>\n\nthere"], TelegramHtml.Split("<b>Hi</b>\n\nthere", 4000));
    }

    [Fact]
    public void Long_messages_split_at_blank_lines_and_keep_quotes_balanced()
    {
        var paragraph = string.Join(" ", Enumerable.Repeat("word &amp; <b>bold</b>", 20));
        var quote = "<blockquote expandable>" + string.Join("\n", Enumerable.Repeat(paragraph, 12)) + "</blockquote>";
        var html = string.Join("\n\n", Enumerable.Repeat(paragraph, 6)) + "\n\n" + quote + "\n\n<i>end</i>";

        var chunks = TelegramHtml.Split(html, 1000);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c =>
        {
            Assert.True(c.Length <= 1000, $"{c.Length} chars");
            Assert.Empty(TelegramHtml.Problems(c));
            Assert.False(c.StartsWith('\n') || c.EndsWith('\n'));
        });
        // The quote is cut inside, so each piece of it is closed and reopened.
        Assert.True(chunks.Count(c => c.Contains("<blockquote expandable>", StringComparison.Ordinal)) > 1);
        // Nothing is lost: the visible text is the same.
        Assert.Equal(
            Html.ToPlain(html).Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal),
            string.Concat(chunks.Select(c => Html.ToPlain(c).Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal))));
    }

    [Fact]
    public void A_line_longer_than_a_chunk_is_cut_at_a_space_outside_tags_and_entities()
    {
        var line = string.Join(" ", Enumerable.Repeat("a&amp;b <b>x y</b>", 200));

        var chunks = TelegramHtml.Split(line, 500);

        Assert.All(chunks, c =>
        {
            Assert.True(c.Length <= 500);
            Assert.Empty(TelegramHtml.Problems(c));
        });
    }

    [Theory]
    [InlineData("<b>ok</b> &amp; &lt;fine&gt;")]
    [InlineData("<blockquote expandable>a\n<i>b</i></blockquote>")]
    public void Valid_html_has_no_problems(string html) => Assert.Empty(TelegramHtml.Problems(html));

    [Theory]
    [InlineData("HR < 150")]
    [InlineData("salt & pepper")]
    [InlineData("<b>open")]
    [InlineData("<b><i>x</b></i>")]
    [InlineData("<h1>x</h1>")]
    public void Invalid_html_is_reported(string html) => Assert.NotEmpty(TelegramHtml.Problems(html));
}
