using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoachBot.Core.Services;

public sealed record PlanGenerationResult(TrainingPlan? Plan, string? FailureKey);

public sealed class PlanService(
    ICoachDb db,
    AthleteService athletes,
    ICorosGateway gateway,
    WatchService watch,
    CoachLlm llm,
    MessageFormatter fmt,
    TimeProvider time,
    ILogger<PlanService> logger)
{
    public Task<TrainingPlan?> GetCurrentPlanAsync(Guid athleteId, CancellationToken ct) =>
        db.TrainingPlans
            .Include(p => p.Workouts)
            .Where(p => p.AthleteId == athleteId && (p.Status == PlanStatus.Active || p.Status == PlanStatus.Paused))
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

    /// <summary>The latest proposal still waiting for the athlete's approval, without its workouts.</summary>
    public Task<TrainingPlan?> GetPendingPlanAsync(Guid athleteId, CancellationToken ct) =>
        db.TrainingPlans
            .Where(p => p.AthleteId == athleteId && p.Status == PlanStatus.PendingApproval)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The LLM diagnoses the athlete, then designs the whole plan: weeks, sessions, their structure and the pace table.
    /// Code only checks that the design can be stored (<see cref="PlanDesignMapper"/>); everything else is the coach's
    /// choice. Throws <see cref="LlmException"/> when either answer is unusable, and then nothing is stored.
    /// </summary>
    public async Task<PlanGenerationResult> GenerateAsync(Athlete athlete, CancellationToken ct)
    {
        var goal = await athletes.GetActiveGoalAsync(athlete.Id, ct);
        if (goal is null)
        {
            return new(null, "plan.no_goal");
        }

        var today = AthleteClock.Today(time, athlete.TimeZone);
        var start = PlanCalendar.MondayAfter(today);
        var weeks = PlanCalendar.WeeksUntil(start, goal.RaceDate);
        if (weeks < 1)
        {
            return new(null, "plan.infeasible_race_too_soon");
        }

        var lang = athlete.Language;
        var history = await LoadHistoryAsync(athlete, today, goal.DistanceKm, ct);
        var notesSince = time.GetUtcNow().AddDays(-30);
        var notes = await db.Feedback.Where(f => f.AthleteId == athlete.Id && f.Notes != null && f.CreatedAt >= notesSince)
            .OrderByDescending(f => f.CreatedAt).Select(f => f.Notes!).Take(10).ToListAsync(ct);

        var assessment = await llm.AskAsync<AthleteAssessment>("athlete_assessment", athlete.Id, new
        {
            race = new { goal.Name, date = goal.RaceDate, distance_km = goal.DistanceKm, goal_time_seconds = goal.GoalTimeSeconds, weeks_to_race = weeks },
            athlete = new
            {
                run_days_per_week = athlete.RunDaysPerWeek,
                age = athlete.BirthYear is int y ? today.Year - y : (int?)null,
                max_hr = athlete.MaxHr,
                profile_notes = athlete.Notes,
                recent_notes = notes,
            },
            fitness = history,
        }, lang, LlmSchemas.AthleteAssessment, ct, AssessmentErrors);

        var design = await llm.AskAsync<PlanDesign>("plan_design", athlete.Id, new
        {
            race = new
            {
                goal.Name,
                date = goal.RaceDate,
                weekday = goal.RaceDate.DayOfWeek.ToString().ToLowerInvariant()[..3],
                distance_km = goal.DistanceKm,
                goal_time_seconds = goal.GoalTimeSeconds,
            },
            assessment,
            athlete = new
            {
                run_days_per_week = athlete.RunDaysPerWeek,
                long_run_day = athlete.LongRunDay.ToString().ToLowerInvariant()[..3],
                profile_notes = athlete.Notes,
                recent_notes = notes,
            },
            fitness = history,
            plan_window = new { first_monday = start, weeks, race_week = weeks },
        }, lang, LlmSchemas.PlanDesign, ct, d => PlanDesignMapper.Map(d, start, goal.RaceDate, goal.DistanceKm).Errors);

        var mapped = PlanDesignMapper.Map(design, start, goal.RaceDate, goal.DistanceKm);
        foreach (var w in mapped.Workouts)
        {
            fmt.ComposeTexts(lang, w, w.Title, w.Description);
        }

        var focus = (design.Weeks ?? []).Where(w => !string.IsNullOrWhiteSpace(w.Focus)).Select(w => new WeeklyFocus(w.Week, w.Focus!)).ToList();
        var snapshot = new PlanSnapshot(mapped.Paces, goal.DistanceKm, history.WeeklyKmLast4WeeksAvg, history.LongestRunLast4WeeksKm,
            history.WeeksOfHistory, history.Current?.Vo2Max, focus, assessment, history.Current);

        var previous = await db.TrainingPlans.Where(p => p.AthleteId == athlete.Id).MaxAsync(p => (int?)p.Version, ct) ?? 0;
        foreach (var pending in await db.TrainingPlans.Where(p => p.AthleteId == athlete.Id && p.Status == PlanStatus.PendingApproval).ToListAsync(ct))
        {
            pending.Status = PlanStatus.Superseded;
        }

        var plan = new TrainingPlan
        {
            AthleteId = athlete.Id,
            RaceGoalId = goal.Id,
            Version = previous + 1,
            Status = PlanStatus.PendingApproval,
            StartDate = start,
            EndDate = goal.RaceDate,
            GeneratedBy = $"llm:{llm.ProviderName}",
            Rationale = design.Rationale,
            WarningsJson = "[]",
            InputsJson = CoachJson.Serialize(snapshot),
            CreatedAt = time.GetUtcNow(),
        };
        foreach (var w in mapped.Workouts)
        {
            w.PlanId = plan.Id;
            w.UpdatedAt = time.GetUtcNow();
            plan.Workouts.Add(w);
        }

        db.TrainingPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Plan v{Version} designed by {GeneratedBy} ({Workouts} workouts, {Weeks} weeks)", plan.Version, plan.GeneratedBy,
            plan.Workouts.Count, weeks);
        return new(plan, null);
    }

    private static IReadOnlyList<string> AssessmentErrors(AthleteAssessment a)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(a.Summary))
        {
            errors.Add("assessment_summary_missing");
        }

        if (a.PredictedRaceSeconds is < 0 or > 0 and < 300 or > 100_000)
        {
            errors.Add($"predicted_race_seconds_invalid: {a.PredictedRaceSeconds}; seconds, or 0 when the data is too thin");
        }

        return errors;
    }

    /// <returns>
    /// The activated plan (null if it was not pending) and the replaced plan's future workouts already on the COROS
    /// calendar: COROS tools cannot delete them, so the athlete has to remove them in the app.
    /// </returns>
    public async Task<(TrainingPlan? Plan, IReadOnlyList<PlannedWorkout> OrphanedOnCoros)> ApproveAsync(Guid athleteId, Guid planId, CancellationToken ct)
    {
        var plan = await db.TrainingPlans.FirstOrDefaultAsync(p => p.Id == planId && p.AthleteId == athleteId, ct);
        if (plan is null || plan.Status != PlanStatus.PendingApproval)
        {
            return (null, []);
        }

        var orphaned = new List<PlannedWorkout>();
        foreach (var current in await db.TrainingPlans.Include(p => p.Workouts)
                     .Where(p => p.AthleteId == athleteId && (p.Status == PlanStatus.Active || p.Status == PlanStatus.Paused)).ToListAsync(ct))
        {
            current.Status = PlanStatus.Superseded;
            orphaned.AddRange(current.Workouts.Where(w => w.CorosSyncStatus == SyncStatus.Synced && w.Date >= plan.StartDate));
        }

        plan.Status = PlanStatus.Active;
        plan.ApprovedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return (plan, orphaned);
    }

    public async Task<bool> RejectAsync(Guid athleteId, Guid planId, CancellationToken ct)
    {
        var plan = await db.TrainingPlans.FirstOrDefaultAsync(p => p.Id == planId && p.AthleteId == athleteId, ct);
        if (plan is null || plan.Status != PlanStatus.PendingApproval)
        {
            return false;
        }

        plan.Status = PlanStatus.Rejected;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public OutboundMessage RenderProposal(Athlete athlete, TrainingPlan plan, RaceGoal goal)
    {
        var lang = athlete.Language;
        var weeks = plan.Workouts.GroupBy(w => w.WeekIndex).OrderBy(g => g.Key).ToList();
        var peak = weeks.Max(g => g.Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm));
        var longest = plan.Workouts.Where(w => w.Type == WorkoutType.Long).Select(w => w.TargetDistanceKm).DefaultIfEmpty(0).Max();
        var taperWeeks = weeks.Count(g => g.Any(w => w.Phase == PlanPhase.Taper));
        var firstWeek = weeks[0].OrderBy(w => w.Date).ToList();
        var snapshot = PlanSnapshot.Of(plan);
        var assessment = snapshot?.Assessment;

        var prediction = assessment?.PredictedRaceSeconds is int predicted and > 0
            ? goal.GoalTimeSeconds is int goalSeconds
                ? fmt.T(lang, "plan.goal_prediction", MessageFormatter.Time(goalSeconds), MessageFormatter.Time(predicted))
                : fmt.T(lang, "plan.prediction", MessageFormatter.Time(predicted))
            : Html.Empty;
        var verdict = assessment?.GoalVerdict is "realistic" or "ambitious" or "unrealistic" && goal.GoalTimeSeconds is not null
            ? fmt.T(lang, $"plan.verdict.{assessment.GoalVerdict}")
            : Html.Empty;
        var warnings = (CoachJson.Deserialize<List<string>>(plan.WarningsJson) ?? []).Select(w => new Html("⚠️ " + fmt.T(lang, w).Value));

        var text = Html.Blocks(
            Html.Lines(
                fmt.T(lang, "plan.proposal_title", goal.Name),
                fmt.T(lang, "plan.proposal_subtitle", fmt.RaceDistance(lang, goal.DistanceKm), fmt.Day(lang, goal.RaceDate), weeks.Count)),
            Html.Lines(
            [
                prediction,
                verdict,
                fmt.T(lang, "plan.volume", MessageFormatter.Km(weeks[0].Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm)),
                    MessageFormatter.Km(peak), MessageFormatter.Km(longest), taperWeeks),
                .. warnings,
            ]),
            snapshot?.Watch is { } watched ? fmt.Watch(lang, watched, goal.DistanceKm) : Html.Empty,
            Html.Lines(
                fmt.T(lang, "plan.week_heading", MessageFormatter.Dates(firstWeek[0].Date, firstWeek[^1].Date)),
                Html.Lines(firstWeek.Select(w => fmt.Line(lang, w)))),
            assessment is null ? Html.Empty : Html.Expandable(RenderAssessment(lang, assessment)),
            string.IsNullOrWhiteSpace(plan.Rationale)
                ? Html.Empty
                : Html.Expandable(Html.Lines(fmt.T(lang, "plan.rationale_title"), Html.Text(plan.Rationale.Trim()))),
            fmt.T(lang, "disclaimer.short"));
        return new OutboundMessage(text,
        [
            [new MessageButton(fmt.T(lang, "button.approve_plan"), $"plan:approve:{plan.Id}"), new MessageButton(fmt.T(lang, "button.reject"), $"plan:reject:{plan.Id}")],
        ]);
    }

    /// <summary>The LLM's diagnosis: level and summary, goal verdict, then strengths, limiters and risks as lists.</summary>
    public Html RenderAssessment(string lang, AthleteAssessment a)
    {
        var goal = !string.IsNullOrWhiteSpace(a.GoalVerdict) && a.GoalVerdict != "no_goal_time"
            ? Html.Lines(fmt.T(lang, "assess.goal", Label(lang, "assess.verdict.", a.GoalVerdict)),
                string.IsNullOrWhiteSpace(a.GoalComment) ? Html.Empty : Html.Text(a.GoalComment.Trim()))
            : Html.Empty;
        return Html.Blocks(
            Html.Lines(fmt.T(lang, "plan.assessment_title", Label(lang, "assess.level.", a.Level)),
                string.IsNullOrWhiteSpace(a.Summary) ? Html.Empty : Html.Text(a.Summary.Trim())),
            goal,
            a.Strengths is { Count: > 0 } ? Html.Lines(fmt.T(lang, "assess.strengths"), Html.Bullets(a.Strengths)) : Html.Empty,
            a.Limiters is { Count: > 0 }
                ? Html.Lines([fmt.T(lang, "assess.limiters"), .. a.Limiters.Select(l => string.IsNullOrWhiteSpace(l.Evidence)
                    ? new Html("• " + Html.Bold(Label(lang, "assess.limiter.", l.Area)).Value)
                    : fmt.T(lang, "assess.limiter_item", Label(lang, "assess.limiter.", l.Area), l.Evidence.Trim()))])
                : Html.Empty,
            a.Risks is { Count: > 0 } ? Html.Lines(fmt.T(lang, "assess.risks"), Html.Bullets(a.Risks)) : Html.Empty);
    }

    private static readonly HashSet<string> AssessmentLabels =
    [
        "assess.level.beginner", "assess.level.intermediate", "assess.level.advanced",
        "assess.verdict.realistic", "assess.verdict.ambitious", "assess.verdict.unrealistic",
        "assess.limiter.aerobic_base", "assess.limiter.long_run_endurance", "assess.limiter.threshold", "assess.limiter.vo2max",
        "assess.limiter.speed", "assess.limiter.race_specific", "assess.limiter.consistency", "assess.limiter.recovery",
    ];

    private string Label(string lang, string prefix, string? value)
    {
        var key = prefix + value;
        return AssessmentLabels.Contains(key) ? fmt.Text(lang, key) : value ?? "";
    }

    /// <summary>
    /// Everything the plan is built from, as the LLM sees it: raw numbers, summed and counted but never judged. Weekly
    /// values cover the last 8 full weeks before today, oldest first.
    /// </summary>
    private sealed record History(
        IReadOnlyList<double> WeeklyKmOldestFirst,
        IReadOnlyList<double?> WeeklyRunTrainingLoadOldestFirst,
        double WeeklyKmLast4WeeksAvg,
        double LongestRunLast4WeeksKm,
        int WeeksOfHistory,
        IReadOnlyList<RecentRun> RecentRuns,
        WatchFitness? Watch,
        WatchFitness? WatchFourWeeksAgo,
        TrainingLoadState? TrainingLoad,
        RecoveryState? Recovery,
        [property: System.Text.Json.Serialization.JsonIgnore] WatchMetrics? Current);

    /// <summary>A recent run as shown to the LLM, with COROS's own reading of it.</summary>
    private sealed record RecentRun(
        DateOnly Date, double Km, int DurationMinutes, string? Pace, string? BestKmPace, int? AvgHr, int? MaxHr, double? ElevationGainM,
        double? TrainingLoad, double? AerobicTe, double? AnaerobicTe, string? TrainingFocus, string? Performance, string? Name);

    /// <summary>The COROS fitness assessment: times as "H:MM:SS", paces per km.</summary>
    private sealed record WatchFitness(
        DateOnly Date, double? Vo2Max, double? RunningLevel, string? ThresholdPace, string? FiveKPrediction, string? TenKPrediction,
        string? HalfMarathonPrediction, string? MarathonPrediction, int? GoalDistancePredictionSeconds);

    private sealed record TrainingLoadState(
        DateOnly Date, double? ShortTermLoad, double? LongTermLoad, double? LoadRatio, string? Status, double? LongTermLoadFourWeeksAgo,
        IReadOnlyList<LoadDay> LastTwoWeeks);

    private sealed record LoadDay(DateOnly Date, double? ShortTermLoad, double? LongTermLoad, double? LoadRatio, string? Status);

    private sealed record RecoveryState(DateOnly? RecoveryDate, int? RecoveryPct, double? RestingHrWeekAvg, double? HrvWeekAvg, double? SleepHoursWeekAvg,
        double? StressWeekAvg);

    private async Task<History> LoadHistoryAsync(Athlete athlete, DateOnly today, double goalKm, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-56);
        var runs = (await db.Activities.Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source && a.StartTime >= since).ToListAsync(ct))
            .Where(a => a.IsRun)
            .Select(a => (Date: AthleteClock.LocalDate(a.StartTime, athlete.TimeZone), Run: a))
            .Where(r => r.Date < today)
            .ToList();

        var weeksOfHistory = runs.Count == 0 ? 0 : Math.Min(8, (today.DayNumber - runs.Min(r => r.Date).DayNumber) / 7 + 1);
        var last4 = runs.Where(r => r.Date >= today.AddDays(-28)).ToList();
        var divisor = Math.Clamp(weeksOfHistory, 1, 4);
        var weeks = Enumerable.Range(1, 8).Reverse()
            .Select(i => runs.Where(r => r.Date >= today.AddDays(-7 * i) && r.Date < today.AddDays(-7 * (i - 1))).Select(r => r.Run).ToList())
            .ToList();

        static string? Clock(int? seconds) => seconds is int s ? MessageFormatter.Time(s) : null;
        WatchFitness? Fitness(WatchMetrics? w) => w?.FitnessDate is not { } date
            ? null
            : new WatchFitness(date, w.Vo2Max, w.RunningLevel, w.ThresholdPaceSecPerKm is int p ? MessageFormatter.Pace(p) : null,
                Clock(w.FiveKPredictionSeconds), Clock(w.TenKPredictionSeconds), Clock(w.HalfMarathonPredictionSeconds), Clock(w.MarathonPredictionSeconds),
                w.PredictionFor(goalKm));

        var now = await watch.AtAsync(athlete.Id, today, ct);
        var before = await watch.AtAsync(athlete.Id, today.AddDays(-28), ct);
        var metricsFrom = today.AddDays(-13);
        var metrics = await db.DailyMetrics
            .Where(m => m.AthleteId == athlete.Id && m.Source == gateway.Source && m.Date >= metricsFrom && m.Date <= today)
            .OrderBy(m => m.Date)
            .ToListAsync(ct);
        double? Avg(Func<DailyMetric, double?> f) =>
            metrics.Where(m => m.Date > today.AddDays(-7)).Select(f).Where(v => v is not null).DefaultIfEmpty(null).Average() is double v ? Math.Round(v, 1) : null;

        var load = now?.LoadDate is not { } loadDate
            ? null
            : new TrainingLoadState(loadDate, now.ShortTermLoad, now.LongTermLoad, now.LoadRatio, now.LoadStatus, before?.LongTermLoad,
                [.. metrics.Where(m => m.AcuteLoad != null || m.ChronicLoad != null || m.LoadRatio != null)
                    .Select(m => new LoadDay(m.Date, m.AcuteLoad, m.ChronicLoad, m.LoadRatio, m.LoadStatus))]);
        var recovery = new RecoveryState(now?.RecoveryPct is null ? null : metrics.LastOrDefault(m => m.RecoveryPct != null)?.Date, now?.RecoveryPct,
            Avg(m => m.RestingHr), Avg(m => m.HrvMs), Avg(m => m.SleepMinutes / 60.0), Avg(m => m.StressAvg));

        return new History(
            [.. weeks.Select(w => Math.Round(w.Sum(r => r.DistanceKm), 1))],
            [.. weeks.Select(w => w.Any(r => r.TrainingLoad is not null) ? Math.Round(w.Sum(r => r.TrainingLoad ?? 0)) : (double?)null)],
            Math.Round(last4.Sum(r => r.Run.DistanceKm) / divisor, 1),
            Math.Round(last4.Select(r => r.Run.DistanceKm).DefaultIfEmpty(0).Max(), 1),
            weeksOfHistory,
            [.. runs.OrderByDescending(r => r.Date).Take(40).Select(r => new RecentRun(r.Date, Math.Round(r.Run.DistanceKm, 1), r.Run.DurationSeconds / 60,
                r.Run.AvgPaceSecPerKm is int p ? MessageFormatter.Pace(p) : null, r.Run.BestKmPaceSecPerKm is int b ? MessageFormatter.Pace(b) : null,
                r.Run.AvgHr, r.Run.MaxHr, r.Run.ElevationGainM, r.Run.TrainingLoad, r.Run.AerobicTe, r.Run.AnaerobicTe, r.Run.TrainingFocus,
                r.Run.Performance, r.Run.Name))],
            Fitness(now),
            Fitness(before),
            load,
            recovery is { RecoveryPct: null, RestingHrWeekAvg: null, HrvWeekAvg: null, SleepHoursWeekAvg: null, StressWeekAvg: null } ? null : recovery,
            now);
    }
}
