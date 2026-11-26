using System.Globalization;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using Microsoft.EntityFrameworkCore;

namespace CoachBot.Core.Services;

public sealed class ReportService(
    ICoachDb db,
    PlanService plans,
    AthleteService athletes,
    ReadinessService readiness,
    WatchService watch,
    RecommendationService recommendations,
    ICorosGateway gateway,
    AlertService alerts,
    CoachLlm llm,
    MessageFormatter fmt,
    TimeProvider time)
{
    /// <summary>Today's session and the latest readiness verdict. Answers at once: no LLM call.</summary>
    public async Task<OutboundMessage> TodayAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        var check = await readiness.LatestAsync(athlete.Id, ct);

        var readinessBlock = check is null
            ? fmt.T(lang, "readiness.none")
            : fmt.ReadinessBlock(lang, check.Level, check.Confidence, check.Summary, CoachJson.Deserialize<List<string>>(check.ConcernsJson) ?? [],
                check.Date == today ? Html.Empty : fmt.T(lang, "readiness.checked_on", fmt.Day(lang, check.Date)));
        return new OutboundMessage(Html.Blocks(
            fmt.T(lang, "report.today_title", fmt.Day(lang, today)),
            await PlanStateAsync(athlete, plan, ct),
            plan is null ? Html.Empty : Today(lang, plan, today),
            readinessBlock));
    }

    public async Task<OutboundMessage> WeekAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var monday = PlanCalendar.MondayOf(today);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);

        return new OutboundMessage(Html.Blocks(
            fmt.T(lang, "report.week_title", MessageFormatter.Dates(monday, monday.AddDays(6))),
            await PlanStateAsync(athlete, plan, ct),
            plan is null ? Html.Empty : Week(lang, plan, monday)));
    }

    public async Task<OutboundMessage> StatusAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var goal = await athletes.GetActiveGoalAsync(athlete.Id, ct);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct) ?? await plans.GetPendingPlanAsync(athlete.Id, ct);
        var connection = await db.CorosConnections.FirstOrDefaultAsync(c => c.AthleteId == athlete.Id, ct);
        var pending = await db.Recommendations.CountAsync(r => r.AthleteId == athlete.Id && r.Status == RecommendationStatus.Proposed, ct);
        var lastActivity = await db.Activities.Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source).MaxAsync(a => (DateTimeOffset?)a.StartTime, ct);
        var notSet = fmt.T(lang, "common.not_set");
        var current = await watch.AtAsync(athlete.Id, today, ct);

        var coros = connection is not { IsConnected: true }
            ? fmt.T(lang, "status.coros_disconnected")
            : Html.Lines(
                fmt.T(lang, "status.coros_connected",
                    connection.LastSyncAt is { } synced ? Synced(lang, synced, athlete.TimeZone) : "–",
                    lastActivity is { } last ? fmt.Day(lang, AthleteClock.LocalDate(last, athlete.TimeZone)) : "–"),
                connection.LastSyncError is { } error ? fmt.T(lang, "status.coros_error", error) : Html.Empty);
        return new OutboundMessage(Html.Blocks(
            fmt.T(lang, "status.title"),
            Html.Lines(
                goal is null
                    ? fmt.T(lang, "status.no_goal")
                    : fmt.T(lang, "status.goal", goal.Name, fmt.RaceDistance(lang, goal.DistanceKm), fmt.Day(lang, goal.RaceDate),
                        Math.Max(0, (goal.RaceDate.DayNumber - today.DayNumber) / 7), goal.GoalTimeSeconds is int g ? MessageFormatter.Time(g) : notSet),
                plan is null
                    ? fmt.T(lang, "status.no_plan")
                    : fmt.T(lang, "status.plan", fmt.T(lang, $"plan.status.{plan.Status.ToString().ToLowerInvariant()}"),
                        fmt.Day(lang, AthleteClock.LocalDate(plan.CreatedAt, athlete.TimeZone))),
                fmt.T(lang, "status.profile", athlete.RunDaysPerWeek, fmt.DayName(lang, athlete.LongRunDay),
                    athlete.MaxHr is int maxHr ? maxHr.ToString(CultureInfo.InvariantCulture) : notSet),
                coros,
                pending > 0 ? fmt.T(lang, "status.pending", pending) : Html.Empty),
            current is null ? Html.Empty : fmt.Watch(lang, current, goal?.DistanceKm)));
    }

    /// <summary>"06:00 26/11" in the athlete's time zone.</summary>
    private static string Synced(string lang, DateTimeOffset at, string timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(at, AthleteClock.Zone(timeZone));
        return local.ToString("HH:mm dd/MM", MessageFormatter.Culture(lang));
    }

    /// <summary>
    /// Morning check: the LLM judges today's readiness from the raw recovery data and may change the coming sessions.
    /// Without a plan there is nothing to check: returns null and the LLM is not asked. Throws
    /// <see cref="LlmException"/> when the answer is unusable; the missed-session bookkeeping is kept.
    /// </summary>
    public async Task<OutboundMessage?> DailyCheckAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        if (plan is null)
        {
            return null;
        }

        // Bookkeeping, not a decision: a session two days past with no run matched to it was missed. The coach sees
        // these and decides whether anything moves.
        var justMissed = plan.Workouts.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Rest && w.Date <= today.AddDays(-2)).ToList();
        foreach (var w in justMissed)
        {
            w.Status = WorkoutStatus.Missed;
            w.UpdatedAt = time.GetUtcNow();
        }

        if (justMissed.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var goal = await athletes.GetActiveGoalAsync(athlete.Id, ct);
        var snapshot = PlanSnapshot.Of(plan);
        var paces = PlanSnapshot.PacesOf(plan);
        var recovery = await readiness.LoadContextAsync(athlete, today, ct);
        var window = plan.Workouts.Where(w => w.Date >= today.AddDays(-3) && w.Date <= today.AddDays(7)).OrderBy(w => w.Date).ToList();
        var canAdapt = plan.Status == PlanStatus.Active;
        var offered = canAdapt
            ? window.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Race && w.Date >= today).Select(w => w.Id).ToHashSet()
            : [];
        var raceDate = goal?.RaceDate ?? plan.EndDate;

        var ai = await llm.AskAsync<DailyCheckResponse>("daily_check", athlete.Id, new
        {
            today,
            goal = LlmContext.Goal(goal),
            diagnosis = snapshot?.Assessment is { } a ? new { a.Level, a.Limiters, a.Priorities, a.Risks } : null,
            plan = new
            {
                status = plan.Status,
                paces = paces.Entries,
                sessions = window.Select(LlmContext.Session),
                just_missed = justMissed.Select(w => w.Id),
            },
            changes_allowed = canAdapt,
            daily_metrics = recovery.DailyMetrics,
            runs = recovery.Runs,
            feedback = recovery.Feedback,
            recent_readiness = recovery.RecentReadiness,
            athlete_notes = athlete.Notes,
        }, lang, LlmSchemas.DailyCheck, ct, answer =>
        {
            var errors = new List<string>();
            LlmGates.Readiness(answer.Readiness, errors);
            if (string.IsNullOrWhiteSpace(answer.Advice))
            {
                errors.Add("advice_missing");
            }

            if (canAdapt)
            {
                errors.AddRange(WorkoutChangeMapper.Map(answer.Changes, plan, offered, paces, today, raceDate).Errors);
            }

            return errors;
        });

        var (level, confidence) = LlmGates.Readiness(ai.Readiness, []);
        var summary = ai.Readiness!.Summary!.Trim();
        var concerns = ai.Readiness.Concerns?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList() ?? [];
        await readiness.UpsertAsync(athlete.Id, today, level, confidence, summary, concerns, "daily_check", ct);
        await db.SaveChangesAsync(ct);

        var changes = canAdapt ? WorkoutChangeMapper.Map(ai.Changes, plan, offered, paces, today, raceDate).Changes : [];
        IReadOnlyList<Recommendation> recs = changes.Count > 0
            ? await recommendations.CreateAsync(athlete, plan, changes, "daily_check", fmt.T(lang, "adapt.from_daily"), null, ct)
            : [];

        if (level != ReadinessLevel.Green)
        {
            await alerts.RaiseAsync(athlete.Id, AlertType.Fatigue, level == ReadinessLevel.Red ? AlertSeverity.Warning : AlertSeverity.Info,
                summary, $"fatigue:{today:yyyy-MM-dd}", ct, send: false);
        }

        var notices = Html.Lines(
            [
                justMissed.Count > 0 ? fmt.T(lang, "daily.missed", justMissed.Count) : Html.Empty,
                .. plan.Workouts.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Rest && w.Date == today.AddDays(-1))
                    .Select(w => fmt.T(lang, "daily.unconfirmed", fmt.Summary(lang, PlanAdjuster.State(w)))),
            ]);

        var buttons = new List<IReadOnlyList<MessageButton>>();
        var blocks = new List<Html>
        {
            fmt.T(lang, "daily.title", fmt.Day(lang, today)),
            await PlanStateAsync(athlete, plan, ct),
            notices,
            fmt.ReadinessBlock(lang, level, confidence, summary, concerns),
            Html.Lines(fmt.T(lang, "daily.today"), Today(lang, plan, today)),
            fmt.T(lang, "daily.advice", ai.Advice!.Trim()),
        };
        foreach (var rec in recs)
        {
            var recMessage = recommendations.Render(athlete, rec);
            blocks.Add(new Html(recMessage.Text));
            buttons.AddRange(recMessage.Buttons ?? []);
        }

        var lastRunWithoutRpe = await LastRunWithoutRpeAsync(athlete, ct);
        if (lastRunWithoutRpe is not null)
        {
            blocks.Add(fmt.T(lang, "daily.ask_rpe", MessageFormatter.Km(Math.Round(lastRunWithoutRpe.DistanceKm, 1))));
            buttons.AddRange(RpeButtons(lastRunWithoutRpe.Id));
        }

        return new OutboundMessage(Html.Blocks(blocks), buttons.Count > 0 ? buttons : null);
    }

    /// <summary>Two rows of 1–10 buttons that record how hard a run felt.</summary>
    public static IReadOnlyList<IReadOnlyList<MessageButton>> RpeButtons(Guid activityId) =>
    [
        [.. Enumerable.Range(1, 5).Select(i => new MessageButton($"{i}", $"rpe:{activityId}:{i}"))],
        [.. Enumerable.Range(6, 5).Select(i => new MessageButton($"{i}", $"rpe:{activityId}:{i}"))],
    ];

    public async Task<OutboundMessage?> WeeklyReportAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var monday = PlanCalendar.MondayOf(today);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);

        var since = time.GetUtcNow().AddDays(-15);
        var activities = (await db.Activities.Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source && a.StartTime >= since).ToListAsync(ct))
            .Where(a => a.IsRun)
            .Select(a => (Date: AthleteClock.LocalDate(a.StartTime, athlete.TimeZone), a))
            .ToList();
        var thisWeek = activities.Where(x => x.Date >= monday && x.Date <= today).Select(x => x.a).ToList();
        var lastWeek = activities.Where(x => x.Date >= monday.AddDays(-7) && x.Date < monday).Select(x => x.a).ToList();

        var metricFrom = monday.AddDays(-7);
        var metrics = await db.DailyMetrics.Where(m => m.AthleteId == athlete.Id && m.Source == gateway.Source && m.Date >= metricFrom).ToListAsync(ct);
        double? Avg(Func<DailyMetric, double?> f, DateOnly from, DateOnly to) =>
            metrics.Where(m => m.Date >= from && m.Date <= to).Select(f).Where(v => v is not null).DefaultIfEmpty(null).Average();

        var planned = plan?.Workouts.Where(w => w.Date >= monday && w.Date <= monday.AddDays(6) && w.Type != WorkoutType.Rest).ToList() ?? [];
        var stats = new
        {
            km = Math.Round(thisWeek.Sum(a => a.DistanceKm), 1),
            planned_km = Math.Round(planned.Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm), 1),
            last_week_km = Math.Round(lastWeek.Sum(a => a.DistanceKm), 1),
            runs = thisWeek.Count,
            completed = planned.Count(w => w.Status == WorkoutStatus.Completed),
            planned_sessions = planned.Count,
            missed = planned.Count(w => w.Status == WorkoutStatus.Missed),
            hrv = Avg(m => m.HrvMs, monday, today),
            hrv_prev = Avg(m => m.HrvMs, monday.AddDays(-7), monday.AddDays(-1)),
            rhr = Avg(m => m.RestingHr, monday, today),
            rhr_prev = Avg(m => m.RestingHr, monday.AddDays(-7), monday.AddDays(-1)),
            sleep_h = Avg(m => m.SleepMinutes / 60.0, monday, today),
        };

        if (stats.runs == 0 && plan is null)
        {
            return null;
        }

        string F(double? v) => v is double d ? d.ToString("0.#", CultureInfo.InvariantCulture) : "–";
        var numbers = Html.Lines(
            fmt.T(lang, "weekly.volume", F(stats.km), F(stats.planned_km), F(stats.last_week_km)),
            plan is null ? Html.Empty : fmt.T(lang, "weekly.sessions", stats.completed, stats.planned_sessions, stats.missed),
            Recovery(lang, (stats.hrv, stats.hrv_prev, "weekly.hrv"), (stats.rhr, stats.rhr_prev, "weekly.rhr"), (stats.sleep_h, null, "weekly.sleep")));

        var planInputs = plan is null ? null : PlanSnapshot.Of(plan);
        var ai = await llm.AskAsync<WeeklyReportResponse>("weekly_report", athlete.Id, new
        {
            stats,
            phase = planned.FirstOrDefault()?.Phase,
            goal_distance_km = planInputs?.RaceKm,
            diagnosis = planInputs?.Assessment is { } a ? new { a.Limiters, a.Priorities } : null,
            readiness_last_7_days = (await readiness.RecentAsync(athlete.Id, today.AddDays(-6), today, ct)).Select(r => new { r.Date, r.Level }),
        }, lang, LlmSchemas.WeeklyReport, ct, r => string.IsNullOrWhiteSpace(r.Summary) ? ["summary_missing"] : []);

        var nextMonday = monday.AddDays(7);
        return new OutboundMessage(Html.Blocks(
            fmt.T(lang, "weekly.title", MessageFormatter.Dates(monday, monday.AddDays(6))),
            numbers,
            Html.Expandable(Html.Lines(fmt.T(lang, "common.notes_title"), Html.Text(ai.Summary.Trim()))),
            string.IsNullOrWhiteSpace(ai.FocusNextWeek) ? Html.Empty : fmt.T(lang, "weekly.focus", ai.FocusNextWeek.Trim()),
            plan is null || !plan.Workouts.Any(w => w.Date >= nextMonday && w.Date <= nextMonday.AddDays(6))
                ? Html.Empty
                : Html.Lines(fmt.T(lang, "weekly.next_week", MessageFormatter.Dates(nextMonday, nextMonday.AddDays(6))), Sessions(lang, plan, nextMonday, nextMonday.AddDays(6)))));
    }

    private async Task<Activity?> LastRunWithoutRpeAsync(Athlete athlete, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-2);
        var recent = await db.Activities.Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source && a.StartTime >= since).OrderByDescending(a => a.StartTime).ToListAsync(ct);
        foreach (var run in recent.Where(a => a.IsRun))
        {
            if (!await db.Feedback.AnyAsync(f => f.ActivityId == run.Id && f.Rpe != null, ct))
            {
                return run;
            }
        }

        return null;
    }

    /// <summary>"<b>Recovery</b> HRV 45 ms (last week 52) · resting HR 52 · …" with only the values there are data for.</summary>
    private Html Recovery(string lang, params (double? Value, double? Previous, string Key)[] values)
    {
        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var parts = values.Where(v => v.Value is not null).Select(v => v.Previous is double previous
            ? fmt.T(lang, "weekly.with_previous", fmt.T(lang, v.Key, F(v.Value!.Value)), F(previous))
            : fmt.T(lang, v.Key, F(v.Value!.Value))).ToList();
        return parts.Count == 0 ? Html.Empty : fmt.T(lang, "weekly.recovery", new Html(string.Join(" · ", parts.Select(p => p.Value))));
    }

    /// <summary>Why there is no session to show: no plan, a proposal waiting for approval, or a paused plan.</summary>
    private async Task<Html> PlanStateAsync(Athlete athlete, TrainingPlan? plan, CancellationToken ct)
    {
        var lang = athlete.Language;
        if (plan is not null)
        {
            return plan.Status == PlanStatus.Paused ? fmt.T(lang, "report.paused") : Html.Empty;
        }

        return await plans.GetPendingPlanAsync(athlete.Id, ct) is { } pending
            ? fmt.T(lang, "report.pending_plan", fmt.Day(lang, AthleteClock.LocalDate(pending.CreatedAt, athlete.TimeZone)))
            : fmt.T(lang, "report.no_plan");
    }

    /// <summary>Today's session with how to run it, or a rest day.</summary>
    private Html Today(string lang, TrainingPlan plan, DateOnly today)
    {
        var items = plan.Workouts.Where(w => w.Date == today).ToList();
        return items.Count == 0 ? fmt.T(lang, "report.rest_day") : Html.Lines(items.Select(w => fmt.Session(lang, w)));
    }

    private Html Week(string lang, TrainingPlan plan, DateOnly monday)
    {
        var km = plan.Workouts.Where(w => w.Date >= monday && w.Date <= monday.AddDays(6) && w.Type is not (WorkoutType.Rest or WorkoutType.Race))
            .Sum(w => w.TargetDistanceKm);
        var sessions = Sessions(lang, plan, monday, monday.AddDays(6));
        return sessions.IsEmpty
            ? fmt.T(lang, "report.rest_day")
            : Html.Blocks(sessions, fmt.T(lang, "report.week_total", MessageFormatter.Km(km)));
    }

    private Html Sessions(string lang, TrainingPlan plan, DateOnly from, DateOnly to) =>
        Html.Lines(plan.Workouts.Where(w => w.Date >= from && w.Date <= to).OrderBy(w => w.Date).Select(w => fmt.Line(lang, w)));
}
