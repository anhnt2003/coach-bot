using CoachBot.Core.Domain;

namespace CoachBot.Core.Safety;

public sealed record FeedbackPoint(DateOnly Date, int? Rpe, int? Soreness, WorkoutType? WorkoutType);

public sealed record RunLoadPoint(DateOnly Date, double Km, double? TrainingLoad);

public sealed record ReadinessInputs(
    DateOnly Today,
    IReadOnlyList<DailyMetric> Metrics,
    IReadOnlyList<FeedbackPoint> Feedback,
    IReadOnlyList<RunLoadPoint> Runs);

public sealed record ReadinessSignal(string Name, ReadinessLevel Level, double? Value, double? Baseline);

public sealed record ReadinessAssessment(
    ReadinessLevel Level,
    Confidence Confidence,
    IReadOnlyList<ReadinessSignal> Signals,
    IReadOnlyList<string> Missing)
{
    public IEnumerable<ReadinessSignal> Concerns => Signals.Where(s => s.Level != ReadinessLevel.Green);
}

/// <summary>
/// Combines objective recovery markers and subjective feedback into green/amber/red.
/// Missing data is reported and lowers confidence; it is never treated as a good sign.
/// </summary>
public static class ReadinessEvaluator
{
    public const int BaselineDays = 28;
    public const int MinBaselinePoints = 7;

    private static readonly string[] ObjectiveSignals = ["hrv", "resting_hr", "sleep", "recovery", "load_ratio"];

    public static ReadinessAssessment Evaluate(ReadinessInputs input)
    {
        var today = input.Today;
        var metrics = input.Metrics
            .Where(m => m.Date <= today)
            .GroupBy(m => m.Date)
            .ToDictionary(g => g.Key, g => g.First());
        var signals = new List<ReadinessSignal>();

        EvaluateHrv(today, metrics, signals);
        EvaluateRestingHr(today, metrics, signals);
        EvaluateSleep(today, metrics, signals);
        EvaluateRecovery(today, metrics, signals);
        EvaluateLoad(today, metrics, input.Runs, signals);
        EvaluateSubjective(today, input.Feedback, signals);

        var present = signals.Select(s => s.Name).ToHashSet();
        var missing = ObjectiveSignals.Where(s => !present.Contains(s)).ToList();
        var objectiveCount = ObjectiveSignals.Length - missing.Count;
        var confidence = objectiveCount >= 4 ? Confidence.High : objectiveCount >= 2 ? Confidence.Medium : Confidence.Low;

        var level = signals.Select(s => s.Level).DefaultIfEmpty(ReadinessLevel.Green).Max();
        if (signals.Count(s => s.Level == ReadinessLevel.Amber) >= 3)
        {
            level = ReadinessLevel.Red;
        }

        return new ReadinessAssessment(level, confidence, signals, missing);
    }

    private static void EvaluateHrv(DateOnly today, Dictionary<DateOnly, DailyMetric> metrics, List<ReadinessSignal> signals)
    {
        var baseline = Baseline(today, metrics, m => m.HrvMs);
        var recent = Window(today, metrics, m => m.HrvMs, days: 7);
        if (baseline is null || recent.Count == 0 || !HasRecent(today, metrics, m => m.HrvMs, 2))
        {
            return;
        }

        var current = recent.Count >= 3 ? recent.Average() : recent[^1];
        var ratio = current / baseline.Value;
        var level = ratio < 0.80 ? ReadinessLevel.Red : ratio < 0.90 ? ReadinessLevel.Amber : ReadinessLevel.Green;
        signals.Add(new("hrv", level, Math.Round(current, 1), Math.Round(baseline.Value, 1)));
    }

    private static void EvaluateRestingHr(DateOnly today, Dictionary<DateOnly, DailyMetric> metrics, List<ReadinessSignal> signals)
    {
        var baseline = Baseline(today, metrics, m => m.RestingHr);
        var latest = Value(metrics, today, m => m.RestingHr) ?? Value(metrics, today.AddDays(-1), m => m.RestingHr);
        if (baseline is null || latest is null)
        {
            return;
        }

        var todayDelta = Value(metrics, today, m => m.RestingHr) - baseline;
        var yesterdayDelta = Value(metrics, today.AddDays(-1), m => m.RestingHr) - baseline;
        var latestDelta = latest.Value - baseline.Value;

        var level = todayDelta >= 8 && yesterdayDelta >= 8 ? ReadinessLevel.Red
            : latestDelta >= 5 ? ReadinessLevel.Amber
            : ReadinessLevel.Green;
        signals.Add(new("resting_hr", level, latest, Math.Round(baseline.Value, 1)));
    }

    private static void EvaluateSleep(DateOnly today, Dictionary<DateOnly, DailyMetric> metrics, List<ReadinessSignal> signals)
    {
        var lastNight = Value(metrics, today, m => m.SleepMinutes);
        var nightBefore = Value(metrics, today.AddDays(-1), m => m.SleepMinutes);
        if (lastNight is null && nightBefore is null)
        {
            return;
        }

        var level = lastNight < 360 && nightBefore < 360 ? ReadinessLevel.Amber : ReadinessLevel.Green;
        signals.Add(new("sleep", level, lastNight ?? nightBefore, null));
    }

    private static void EvaluateRecovery(DateOnly today, Dictionary<DateOnly, DailyMetric> metrics, List<ReadinessSignal> signals)
    {
        var recovery = Value(metrics, today, m => m.RecoveryPct) ?? Value(metrics, today.AddDays(-1), m => m.RecoveryPct);
        if (recovery is null)
        {
            return;
        }

        var level = recovery < 25 ? ReadinessLevel.Red : recovery < 50 ? ReadinessLevel.Amber : ReadinessLevel.Green;
        signals.Add(new("recovery", level, recovery, null));
    }

    private static void EvaluateLoad(
        DateOnly today, Dictionary<DateOnly, DailyMetric> metrics, IReadOnlyList<RunLoadPoint> runs, List<ReadinessSignal> signals)
    {
        var ratio = Value(metrics, today, m => m.LoadRatio)
            ?? Value(metrics, today.AddDays(-1), m => m.LoadRatio)
            ?? Value(metrics, today.AddDays(-2), m => m.LoadRatio)
            ?? ComputeAcuteChronicRatio(today, runs);
        if (ratio is null)
        {
            return;
        }

        var level = ratio > 1.5 ? ReadinessLevel.Red : ratio > 1.3 ? ReadinessLevel.Amber : ReadinessLevel.Green;
        signals.Add(new("load_ratio", level, Math.Round(ratio.Value, 2), null));
    }

    private static void EvaluateSubjective(DateOnly today, IReadOnlyList<FeedbackPoint> feedback, List<ReadinessSignal> signals)
    {
        var recent = feedback.Where(f => f.Date >= today.AddDays(-2) && f.Date <= today).ToList();
        var soreness = recent.Select(f => f.Soreness).Where(s => s is not null).DefaultIfEmpty(null).Max();
        if (soreness is not null)
        {
            signals.Add(new("soreness", soreness >= 7 ? ReadinessLevel.Amber : ReadinessLevel.Green, soreness, null));
        }

        var easyRpe = recent
            .Where(f => f.WorkoutType is WorkoutType.Easy or WorkoutType.Recovery && f.Rpe is not null)
            .Select(f => f.Rpe)
            .DefaultIfEmpty(null)
            .Max();
        if (easyRpe is not null)
        {
            signals.Add(new("rpe", easyRpe >= 7 ? ReadinessLevel.Amber : ReadinessLevel.Green, easyRpe, null));
        }
    }

    /// <summary>Acute (7-day) vs chronic (28-day weekly average) load; needs at least 3 weeks of history.</summary>
    internal static double? ComputeAcuteChronicRatio(DateOnly today, IReadOnlyList<RunLoadPoint> runs)
    {
        var window = runs.Where(r => r.Date <= today && r.Date > today.AddDays(-28)).ToList();
        if (window.Count == 0 || runs.Min(r => r.Date) > today.AddDays(-21))
        {
            return null;
        }

        var useLoad = window.All(r => r.TrainingLoad is not null);
        double Load(RunLoadPoint r) => useLoad ? r.TrainingLoad!.Value : r.Km;

        var acute = window.Where(r => r.Date > today.AddDays(-7)).Sum(Load);
        var chronicWeekly = window.Sum(Load) / 4.0;
        return chronicWeekly > 0 ? acute / chronicWeekly : null;
    }

    private static double? Baseline<T>(DateOnly today, Dictionary<DateOnly, DailyMetric> metrics, Func<DailyMetric, T?> selector)
        where T : struct, IConvertible
    {
        var values = Enumerable.Range(1, BaselineDays)
            .Select(i => Value(metrics, today.AddDays(-i), selector))
            .Where(v => v is not null)
            .Select(v => Convert.ToDouble(v!.Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        return values.Count >= MinBaselinePoints ? values.Average() : null;
    }

    private static List<double> Window<T>(DateOnly today, Dictionary<DateOnly, DailyMetric> metrics, Func<DailyMetric, T?> selector, int days)
        where T : struct, IConvertible =>
        Enumerable.Range(0, days)
            .Select(i => Value(metrics, today.AddDays(-(days - 1) + i), selector))
            .Where(v => v is not null)
            .Select(v => Convert.ToDouble(v!.Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    private static bool HasRecent<T>(DateOnly today, Dictionary<DateOnly, DailyMetric> metrics, Func<DailyMetric, T?> selector, int days)
        where T : struct =>
        Enumerable.Range(0, days).Any(i => Value(metrics, today.AddDays(-i), selector) is not null);

    private static T? Value<T>(Dictionary<DateOnly, DailyMetric> metrics, DateOnly date, Func<DailyMetric, T?> selector)
        where T : struct =>
        metrics.TryGetValue(date, out var m) ? selector(m) : null;
}
