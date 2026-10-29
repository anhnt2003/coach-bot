using System.Text;
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
        var history = await LoadHistoryAsync(athlete, today, ct);
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
            history.WeeksOfHistory, history.Vo2Max, focus, assessment);

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
        var sb = new StringBuilder();
        var weeks = plan.Workouts.GroupBy(w => w.WeekIndex).OrderBy(g => g.Key).ToList();
        var peak = weeks.Max(g => g.Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm));
        var longest = plan.Workouts.Where(w => w.Type == WorkoutType.Long).Select(w => w.TargetDistanceKm).DefaultIfEmpty(0).Max();
        var snapshot = PlanSnapshot.Of(plan);

        var taperWeeks = weeks.Count(g => g.Any(w => w.Phase == PlanPhase.Taper));
        sb.AppendLine(fmt.T(lang, "plan.proposal_header", goal.Name, fmt.RaceDistance(lang, goal.DistanceKm), fmt.Day(lang, goal.RaceDate), weeks.Count));
        sb.AppendLine(fmt.T(lang, "plan.proposal_stats", MessageFormatter.Km(weeks[0].Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm)),
            MessageFormatter.Km(peak), MessageFormatter.Km(longest), taperWeeks));
        sb.AppendLine(fmt.T(lang, "plan.designed_by_llm"));
        if (snapshot?.Assessment is { } a)
        {
            if (a.PredictedRaceSeconds is int predicted and > 0)
            {
                sb.AppendLine(goal.GoalTimeSeconds is int goalSeconds
                    ? fmt.T(lang, "plan.prediction_goal", MessageFormatter.Time(predicted), MessageFormatter.Time(goalSeconds))
                    : fmt.T(lang, "plan.prediction", MessageFormatter.Time(predicted)));
            }

            sb.AppendLine().Append(RenderAssessment(lang, a));
        }

        foreach (var warning in CoachJson.Deserialize<List<string>>(plan.WarningsJson) ?? [])
        {
            sb.AppendLine("⚠️ " + fmt.T(lang, warning));
        }

        if (!string.IsNullOrWhiteSpace(plan.Rationale))
        {
            sb.AppendLine().AppendLine(plan.Rationale);
        }

        sb.AppendLine().AppendLine(fmt.T(lang, "plan.first_week"));
        foreach (var w in weeks[0].OrderBy(w => w.Date))
        {
            sb.AppendLine("• " + fmt.Line(lang, w));
        }

        sb.AppendLine().AppendLine(fmt.T(lang, "disclaimer"));
        return new OutboundMessage(sb.ToString().TrimEnd(),
        [
            [new MessageButton(fmt.T(lang, "button.approve_plan"), $"plan:approve:{plan.Id}"), new MessageButton(fmt.T(lang, "button.reject"), $"plan:reject:{plan.Id}")],
        ]);
    }

    /// <summary>The LLM's diagnosis in a few lines; unknown enum values from the LLM are shown as given.</summary>
    public string RenderAssessment(string lang, AthleteAssessment a)
    {
        var sb = new StringBuilder();
        sb.AppendLine(fmt.T(lang, "assess.header", Label(lang, "assess.level.", a.Level)) + (string.IsNullOrWhiteSpace(a.Summary) ? "" : " " + a.Summary));
        if (!string.IsNullOrWhiteSpace(a.GoalVerdict) && a.GoalVerdict != "no_goal_time")
        {
            sb.AppendLine(fmt.T(lang, "assess.goal", Label(lang, "assess.verdict.", a.GoalVerdict)) + (string.IsNullOrWhiteSpace(a.GoalComment) ? "" : " " + a.GoalComment));
        }

        if (a.Strengths is { Count: > 0 })
        {
            sb.AppendLine(fmt.T(lang, "assess.strengths", string.Join("; ", a.Strengths)));
        }

        if (a.Limiters is { Count: > 0 })
        {
            sb.AppendLine(fmt.T(lang, "assess.limiters", string.Join("; ", a.Limiters.Select(l =>
                string.IsNullOrWhiteSpace(l.Evidence) ? Label(lang, "assess.limiter.", l.Area) : $"{Label(lang, "assess.limiter.", l.Area)} ({l.Evidence})"))));
        }

        if (a.Risks is { Count: > 0 })
        {
            sb.AppendLine(fmt.T(lang, "assess.risks", string.Join("; ", a.Risks)));
        }

        return sb.ToString();
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
        return AssessmentLabels.Contains(key) ? fmt.T(lang, key) : value ?? "";
    }

    /// <summary>Training history as the LLM sees it: raw numbers, summed and counted but never judged.</summary>
    private sealed record History(
        IReadOnlyList<double> WeeklyKmOldestFirst,
        double WeeklyKmLast4WeeksAvg,
        double LongestRunLast4WeeksKm,
        int WeeksOfHistory,
        IReadOnlyList<RecentRun> RecentRuns,
        DateOnly? CorosFitnessDate,
        int? CorosThresholdPaceSecPerKm,
        int? CorosMarathonPredictionSeconds,
        double? Vo2Max);

    /// <summary>A recent run as shown to the LLM.</summary>
    private sealed record RecentRun(DateOnly Date, double Km, int DurationMinutes, string? Pace, int? AvgHr, string? Name);

    private async Task<History> LoadHistoryAsync(Athlete athlete, DateOnly today, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-56);
        var runs = (await db.Activities.Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source && a.StartTime >= since).ToListAsync(ct))
            .Where(a => a.IsRun)
            .Select(a => (Date: AthleteClock.LocalDate(a.StartTime, athlete.TimeZone), a.DistanceKm, a.DurationSeconds, a.AvgPaceSecPerKm, a.AvgHr, a.Name))
            .Where(r => r.Date < today)
            .ToList();

        var weeksOfHistory = runs.Count == 0 ? 0 : Math.Min(8, (today.DayNumber - runs.Min(r => r.Date).DayNumber) / 7 + 1);
        var last4 = runs.Where(r => r.Date >= today.AddDays(-28)).ToList();
        var divisor = Math.Clamp(weeksOfHistory, 1, 4);

        var fitnessSince = today.AddDays(-30);
        var fitness = await db.DailyMetrics
            .Where(m => m.AthleteId == athlete.Id && m.Source == gateway.Source && m.Date >= fitnessSince && (m.ThresholdPaceSecPerKm != null || m.MarathonPredictionSeconds != null || m.Vo2Max != null))
            .OrderByDescending(m => m.Date)
            .FirstOrDefaultAsync(ct);

        var weeklyKm = Enumerable.Range(1, 8).Reverse()
            .Select(i => Math.Round(runs.Where(r => r.Date >= today.AddDays(-7 * i) && r.Date < today.AddDays(-7 * (i - 1))).Sum(r => r.DistanceKm), 1))
            .ToList();
        return new History(weeklyKm, Math.Round(last4.Sum(r => r.DistanceKm) / divisor, 1), Math.Round(last4.Select(r => r.DistanceKm).DefaultIfEmpty(0).Max(), 1),
            weeksOfHistory,
            [.. last4.OrderByDescending(r => r.Date).Take(20)
                .Select(r => new RecentRun(r.Date, Math.Round(r.DistanceKm, 1), r.DurationSeconds / 60, r.AvgPaceSecPerKm is int p ? MessageFormatter.Pace(p) : null, r.AvgHr, r.Name))],
            fitness?.Date, fitness?.ThresholdPaceSecPerKm, fitness?.MarathonPredictionSeconds, fitness?.Vo2Max);
    }
}
