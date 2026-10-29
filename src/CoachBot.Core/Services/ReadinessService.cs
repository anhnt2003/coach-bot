using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CoachBot.Core.Services;

public sealed record DailyMetricPoint(
    DateOnly Date, int? RestingHr, double? HrvMs, int? SleepMinutes, int? SleepScore, int? StressAvg, int? RecoveryPct,
    double? AcuteLoad, double? ChronicLoad, double? LoadRatio, int? ThresholdPaceSecPerKm, double? Vo2Max);

public sealed record RunPoint(DateOnly Date, double Km, int DurationMinutes, string? Pace, int? AvgHr, double? TrainingLoad, string? Name);

public sealed record FeedbackPoint(DateOnly Date, int? Rpe, int? Soreness, string? Notes);

public sealed record ReadinessPoint(DateOnly Date, ReadinessLevel Level, Confidence Confidence, string Summary, IReadOnlyList<string> Concerns, string Source);

/// <summary>The recovery data the LLM judges readiness from: raw series, never pre-judged by code.</summary>
public sealed record ReadinessContext(
    IReadOnlyList<DailyMetricPoint> DailyMetrics,
    IReadOnlyList<RunPoint> Runs,
    IReadOnlyList<FeedbackPoint> Feedback,
    IReadOnlyList<ReadinessPoint> RecentReadiness);

/// <summary>Loads the data for the LLM's readiness verdict, and stores and reads those verdicts (readiness_checks).</summary>
public sealed class ReadinessService(ICoachDb db, ICorosGateway gateway, TimeProvider time)
{
    public const int MetricDays = 28;
    public const int FeedbackDays = 7;
    public const int RecentReadinessDays = 3;

    public async Task<ReadinessContext> LoadContextAsync(Athlete athlete, DateOnly today, CancellationToken ct)
    {
        var from = today.AddDays(-(MetricDays - 1));
        var metrics = await db.DailyMetrics
            .Where(m => m.AthleteId == athlete.Id && m.Source == gateway.Source && m.Date >= from && m.Date <= today)
            .OrderBy(m => m.Date)
            .Select(m => new DailyMetricPoint(m.Date, m.RestingHr, m.HrvMs, m.SleepMinutes, m.SleepScore, m.StressAvg, m.RecoveryPct,
                m.AcuteLoad, m.ChronicLoad, m.LoadRatio, m.ThresholdPaceSecPerKm, m.Vo2Max))
            .ToListAsync(ct);

        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var runs = (await db.Activities
                .Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source && a.StartTime >= since && Activity.RunSportTypes.Contains(a.SportType))
                .OrderBy(a => a.StartTime)
                .ToListAsync(ct))
            .Select(a => new RunPoint(AthleteClock.LocalDate(a.StartTime, athlete.TimeZone), Math.Round(a.DistanceKm, 1), a.DurationSeconds / 60,
                a.AvgPaceSecPerKm is int p ? MessageFormatter.Pace(p) : null, a.AvgHr, a.TrainingLoad, a.Name))
            .Where(r => r.Date >= from && r.Date <= today)
            .ToList();

        var feedbackFrom = today.AddDays(-(FeedbackDays - 1));
        var feedback = await db.Feedback
            .Where(f => f.AthleteId == athlete.Id && f.Date >= feedbackFrom && (f.Rpe != null || f.Soreness != null || f.Notes != null))
            .OrderBy(f => f.CreatedAt)
            .Select(f => new FeedbackPoint(f.Date, f.Rpe, f.Soreness, f.Notes))
            .ToListAsync(ct);

        return new ReadinessContext(metrics, runs, feedback, await RecentAsync(athlete.Id, today.AddDays(-RecentReadinessDays), today, ct));
    }

    /// <summary>Stores the verdict for a day, replacing an earlier one. The caller saves.</summary>
    public async Task UpsertAsync(
        Guid athleteId, DateOnly date, ReadinessLevel level, Confidence confidence, string summary, IReadOnlyList<string> concerns, string source,
        CancellationToken ct)
    {
        var check = await db.ReadinessChecks.FirstOrDefaultAsync(r => r.AthleteId == athleteId && r.Date == date, ct);
        if (check is null)
        {
            check = new ReadinessCheck { AthleteId = athleteId, Date = date };
            db.ReadinessChecks.Add(check);
        }

        check.Level = level;
        check.Confidence = confidence;
        check.Summary = summary;
        check.ConcernsJson = CoachJson.Serialize(concerns);
        check.Source = source;
        check.CreatedAt = time.GetUtcNow();
    }

    public Task<ReadinessCheck?> LatestAsync(Guid athleteId, CancellationToken ct) =>
        db.ReadinessChecks.Where(r => r.AthleteId == athleteId).OrderByDescending(r => r.Date).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ReadinessPoint>> RecentAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct) =>
        [.. (await db.ReadinessChecks.Where(r => r.AthleteId == athleteId && r.Date >= from && r.Date <= to).OrderBy(r => r.Date).ToListAsync(ct))
            .Select(r => new ReadinessPoint(r.Date, r.Level, r.Confidence, r.Summary, CoachJson.Deserialize<List<string>>(r.ConcernsJson) ?? [], r.Source))];
}

/// <summary>How plans and goals are shown to the LLM, shared by the prompts that may change the plan.</summary>
public static class LlmContext
{
    public static object? Goal(RaceGoal? goal) =>
        goal is null ? null : new { goal.Name, date = goal.RaceDate, distance_km = goal.DistanceKm, goal_time_seconds = goal.GoalTimeSeconds };

    public static object Session(PlannedWorkout w) => new
    {
        workout_id = w.Id,
        w.Date,
        weekday = w.Date.DayOfWeek.ToString().ToLowerInvariant()[..3],
        w.Type,
        w.Title,
        km = w.TargetDistanceKm,
        w.Phase,
        cutback = w.IsCutbackWeek,
        w.Status,
        description = w.Description,
        pace = w.PaceMinSecPerKm is null ? null : $"{MessageFormatter.Pace(w.PaceMinSecPerKm)}–{MessageFormatter.Pace(w.PaceMaxSecPerKm)}/km",
        hr = w.HrMin is null ? null : $"{w.HrMin}–{w.HrMax}",
    };
}
