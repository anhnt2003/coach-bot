using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoachBot.Core.Abstractions;

namespace CoachBot.Infrastructure.Coros;

/// <summary>
/// Readers for the text the COROS MCP read tools return. Verified live (2026-10-22, `coros discover`): no tool
/// publishes an output schema, and every read tool answers with human-readable text such as
/// "Duration: 45:16 | Distance: 7.02 km". Days without data were observed for sleep, sleep HRV and resting HR, days
/// with values were not, so those readers accept the obvious labelled variants and never guess unlabelled numbers.
/// </summary>
public static partial class CorosText
{
    public sealed record LoadDay(double? ShortTerm, double? LongTerm, double? Ratio);

    public sealed record SleepDay(int? Score, int? Minutes);

    /// <summary>querySportRecords: numbered records ("2. Outdoor Run — 2026-10-21") with labelled fields.</summary>
    public static IReadOnlyList<ActivitySample> SportRecords(string text)
    {
        var headers = RecordHeader().Matches(text);
        var result = new List<ActivitySample>();
        for (var i = 0; i < headers.Count; i++)
        {
            var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            var block = text[headers[i].Index..end];
            var id = Group(LabelId(), block);
            var start = Group(StartTimestamp(), block) is { } epoch ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(epoch, CultureInfo.InvariantCulture)) : (DateTimeOffset?)null;
            var duration = Group(DurationField(), block) is { } d ? ClockSeconds(d) : null;
            var distanceKm = DistanceMatch(block);
            if (id is null || start is null || duration is null || distanceKm is null)
            {
                continue;
            }

            result.Add(new ActivitySample(
                ExternalId: id,
                SportType: CorosMcpGateway.SportName(Group(SportTypeCode(), block) ?? headers[i].Groups["name"].Value),
                StartTime: start.Value,
                DurationSeconds: duration.Value,
                DistanceMeters: distanceKm.Value * 1000,
                AvgPaceSecPerKm: Group(AveragePace(), block) is { } pace ? ClockSeconds(pace) : null,
                AvgHr: Int(AvgHr(), block),
                MaxHr: Int(MaxHr(), block),
                ElevationGainM: null,
                AvgCadence: null,
                TrainingLoad: null,
                Name: Group(Location(), block)?.Trim() ?? headers[i].Groups["name"].Value.Trim(),
                RawJson: Raw(block)));
        }

        return result;
    }

    /// <summary>queryDailyHealthData: "--- 20261016 ---" day blocks with "Stress: Avg 45" (and resting HR when present).</summary>
    public static IReadOnlyDictionary<DateOnly, (int? StressAvg, int? RestingHr, string Raw)> DailyHealth(string text) =>
        DayBlocks(text).ToDictionary(b => b.Date, b => (Int(DailyStress(), b.Text), Int(RestingHrField(), b.Text), Raw(b.Text)));

    /// <summary>queryRestingHeartRate: "2026-10-22: 52 bpm" or "2026-10-22: No data".</summary>
    public static IReadOnlyDictionary<DateOnly, int> RestingHeartRate(string text) =>
        DayBlocks(text).Select(b => (b.Date, Bpm: Int(Bpm(), b.Text))).Where(x => x.Bpm is > 25 and < 120).ToDictionary(x => x.Date, x => x.Bpm!.Value);

    /// <summary>querySleepHrv: the official daily assessment only (the raw time series that follows is ignored).</summary>
    public static IReadOnlyDictionary<DateOnly, double> SleepHrv(string text)
    {
        var assessment = text.Split("Time Series", 2)[0];
        var result = new Dictionary<DateOnly, double>();
        foreach (var b in DayBlocks(assessment))
        {
            var value = Double(HrvAverage(), b.Text) ?? Double(DateLineMs(), b.Text);
            if (value is > 5 and < 300)
            {
                result.TryAdd(b.Date, value.Value);
            }
        }

        return result;
    }

    /// <summary>querySleepOverview: per wake-up day; "Sleep Score: 0" means no data.</summary>
    public static IReadOnlyDictionary<DateOnly, SleepDay> Sleep(string text) =>
        DayBlocks(text)
            .Select(b => (b.Date, Day: new SleepDay(Int(SleepScore(), b.Text) is int s && s > 0 ? s : null,
                Group(SleepDuration(), b.Text) is { } v ? DurationMinutes(v) : null)))
            .Where(x => x.Day.Score is not null || x.Day.Minutes is not null)
            .ToDictionary(x => x.Date, x => x.Day);

    /// <summary>queryRecoveryStatus: "Recovery: 79%" (current value).</summary>
    public static int? RecoveryPct(string text) => Int(Recovery(), text);

    /// <summary>queryTrainingLoadAssessment: per-day short-term load, long-term load and load ratio.</summary>
    public static IReadOnlyDictionary<DateOnly, LoadDay> TrainingLoad(string text) =>
        DayBlocks(text)
            .Select(b => (b.Date, Day: new LoadDay(Double(ShortTermLoad(), b.Text), Double(LongTermLoad(), b.Text), Double(LoadRatio(), b.Text))))
            .Where(x => x.Day.ShortTerm is not null || x.Day.LongTerm is not null || x.Day.Ratio is not null)
            .ToDictionary(x => x.Date, x => x.Day);

    /// <summary>queryFitnessAssessmentOverview: VO2max, threshold pace and race predictions.</summary>
    public static FitnessSnapshot? Fitness(string text)
    {
        var snapshot = new FitnessSnapshot(
            Vo2Max: Double(Vo2Max(), text),
            ThresholdPaceSecPerKm: Group(ThresholdPace(), text) is { } pace ? ClockSeconds(pace) : null,
            MarathonPredictionSeconds: Group(MarathonPrediction(), text) is { } m ? ClockSeconds(m) : null,
            HalfMarathonPredictionSeconds: Group(HalfMarathonPrediction(), text) is { } h ? ClockSeconds(h) : null,
            LactateThresholdHr: Int(ThresholdHr(), text),
            RawJson: Raw(text));
        return snapshot is { Vo2Max: null, ThresholdPaceSecPerKm: null, MarathonPredictionSeconds: null, HalfMarathonPredictionSeconds: null }
            ? null
            : snapshot;
    }

    /// <summary>"1:14:14" or "45:16" → seconds.</summary>
    internal static int? ClockSeconds(string value)
    {
        var parts = value.Trim().Split(':');
        if (parts.Length is < 2 or > 3 || !parts.All(p => int.TryParse(p, CultureInfo.InvariantCulture, out _)))
        {
            return null;
        }

        var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return n.Length == 3 ? n[0] * 3600 + n[1] * 60 + n[2] : n[0] * 60 + n[1];
    }

    /// <summary>"7h 12min", "7h", "45 min" → minutes; null without a unit.</summary>
    internal static int? DurationMinutes(string value)
    {
        // The pattern is all-optional, so skip the empty matches before the first "h"/"min".
        var m = HoursMinutes().Matches(value).FirstOrDefault(x => x.Groups["h"].Success || x.Groups["m"].Success);
        if (m is null)
        {
            return null;
        }

        return (m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) * 60 : 0)
               + (m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>Splits text into blocks that start at a line beginning with a date (yyyy-MM-dd, yyyyMMdd, or "--- yyyyMMdd ---").</summary>
    private static IEnumerable<(DateOnly Date, string Text)> DayBlocks(string text)
    {
        var starts = DayStart().Matches(text);
        for (var i = 0; i < starts.Count; i++)
        {
            var raw = starts[i].Groups["date"].Value.Replace("-", "", StringComparison.Ordinal);
            if (!DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            var end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            yield return (date, text[starts[i].Index..end]);
        }
    }

    private static double? DistanceMatch(string block)
    {
        var m = Distance().Match(block);
        if (!m.Success || !double.TryParse(m.Groups["v"].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return null;
        }

        return m.Groups["u"].Value == "m" ? v / 1000 : v;
    }

    private static string? Group(Regex regex, string text) => regex.Match(text) is { Success: true } m ? m.Groups["v"].Value : null;

    private static int? Int(Regex regex, string text) =>
        Group(regex, text) is { } v && int.TryParse(v.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture, out var n) ? n : null;

    private static double? Double(Regex regex, string text) =>
        Group(regex, text) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The source text as a JSON string, for the jsonb raw columns.</summary>
    private static string Raw(string text) => JsonSerializer.Serialize(text.Trim());

    [GeneratedRegex(@"^\s*\d+\.\s+(?<name>.+?)\s+[—–-]\s+\d{4}-\d{2}-\d{2}\s*$", RegexOptions.Multiline)]
    private static partial Regex RecordHeader();

    [GeneratedRegex(@"LabelId:\s*(?<v>\d+)")]
    private static partial Regex LabelId();

    [GeneratedRegex(@"SportType:\s*(?<v>\d+)")]
    private static partial Regex SportTypeCode();

    [GeneratedRegex(@"startTimestamp=(?<v>\d+)")]
    private static partial Regex StartTimestamp();

    [GeneratedRegex(@"Duration:\s*(?<v>\d+(?::\d{2}){1,2})")]
    private static partial Regex DurationField();

    [GeneratedRegex(@"Distance:\s*(?<v>[\d.,]+)\s*(?<u>km|m)\b")]
    private static partial Regex Distance();

    [GeneratedRegex(@"Average Pace:\s*(?<v>\d+:\d{2})\s*/km")]
    private static partial Regex AveragePace();

    [GeneratedRegex(@"Avg HR:\s*(?<v>\d+)\s*bpm")]
    private static partial Regex AvgHr();

    [GeneratedRegex(@"Max HR:\s*(?<v>\d+)\s*bpm")]
    private static partial Regex MaxHr();

    [GeneratedRegex(@"Location:\s*(?<v>[^\n]+)")]
    private static partial Regex Location();

    [GeneratedRegex(@"^(?:-{2,}\s*)?(?<date>\d{4}-\d{2}-\d{2}|\d{8})\b", RegexOptions.Multiline)]
    private static partial Regex DayStart();

    [GeneratedRegex(@"Stress:\s*Avg\s*(?<v>\d+)")]
    private static partial Regex DailyStress();

    [GeneratedRegex(@"Resting HR:\s*(?<v>\d+)\s*bpm")]
    private static partial Regex RestingHrField();

    [GeneratedRegex(@"(?<v>\d+)\s*bpm")]
    private static partial Regex Bpm();

    [GeneratedRegex(@"(?:Average|Avg)\.?\s*(?:Sleep\s*)?HRV\s*:\s*(?<v>\d+(?:\.\d+)?)\s*ms", RegexOptions.IgnoreCase)]
    private static partial Regex HrvAverage();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}:\s*(?<v>\d+(?:\.\d+)?)\s*ms", RegexOptions.Multiline)]
    private static partial Regex DateLineMs();

    [GeneratedRegex(@"Sleep Score:\s*(?<v>\d+)")]
    private static partial Regex SleepScore();

    [GeneratedRegex(@"^(?:Main Sleep(?: Duration)?|Total Sleep(?: Time)?|Sleep Duration|Duration)\s*:\s*(?<v>[^\n|]+)", RegexOptions.Multiline)]
    private static partial Regex SleepDuration();

    [GeneratedRegex(@"(?:(?<h>\d+)\s*h(?:ours?)?)?\s*(?:(?<m>\d+)\s*min)?")]
    private static partial Regex HoursMinutes();

    [GeneratedRegex(@"Recovery:\s*(?<v>\d+)\s*%")]
    private static partial Regex Recovery();

    [GeneratedRegex(@"Short-Term Load:\s*(?<v>\d+(?:\.\d+)?)")]
    private static partial Regex ShortTermLoad();

    [GeneratedRegex(@"Long-Term Load:\s*(?<v>\d+(?:\.\d+)?)")]
    private static partial Regex LongTermLoad();

    [GeneratedRegex(@"Load Ratio:\s*(?<v>\d+(?:\.\d+)?)")]
    private static partial Regex LoadRatio();

    [GeneratedRegex(@"VO2max:\s*(?<v>\d+(?:\.\d+)?)")]
    private static partial Regex Vo2Max();

    [GeneratedRegex(@"Threshold Pace:\s*(?<v>\d+:\d{2})")]
    private static partial Regex ThresholdPace();

    [GeneratedRegex(@"Threshold (?:HR|Heart Rate):\s*(?<v>\d+)")]
    private static partial Regex ThresholdHr();

    [GeneratedRegex(@"^Marathon Prediction:\s*(?<v>\d+(?::\d{2}){1,2})", RegexOptions.Multiline)]
    private static partial Regex MarathonPrediction();

    [GeneratedRegex(@"^Half Marathon Prediction:\s*(?<v>\d+(?::\d{2}){1,2})", RegexOptions.Multiline)]
    private static partial Regex HalfMarathonPrediction();
}
