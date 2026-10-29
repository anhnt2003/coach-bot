using System.Globalization;
using System.Text;
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

        var sb = new StringBuilder();
        AppendPlanState(sb, lang, plan);
        AppendWorkouts(sb, lang, plan, today, today, "report.today_header");
        sb.AppendLine();
        if (check is null)
        {
            sb.AppendLine(fmt.T(lang, "readiness.none"));
        }
        else
        {
            AppendReadiness(sb, lang, check.Level, check.Confidence, check.Summary, CoachJson.Deserialize<List<string>>(check.ConcernsJson) ?? [],
                fmt.T(lang, "readiness.checked_on", fmt.Day(lang, check.Date)));
        }

        return new OutboundMessage(sb.ToString().TrimEnd());
    }

    public async Task<OutboundMessage> WeekAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var monday = PlanCalendar.MondayOf(today);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);

        var sb = new StringBuilder();
        AppendPlanState(sb, lang, plan);
        AppendWorkouts(sb, lang, plan, monday, monday.AddDays(6), "report.week_header");
        return new OutboundMessage(sb.ToString().TrimEnd());
    }

    public async Task<OutboundMessage> StatusAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var goal = await athletes.GetActiveGoalAsync(athlete.Id, ct);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        var connection = await db.CorosConnections.FirstOrDefaultAsync(c => c.AthleteId == athlete.Id, ct);
        var pending = await db.Recommendations.CountAsync(r => r.AthleteId == athlete.Id && r.Status == RecommendationStatus.Proposed, ct);
        var lastActivity = await db.Activities.Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source).MaxAsync(a => (DateTimeOffset?)a.StartTime, ct);

        var sb = new StringBuilder();
        sb.AppendLine(goal is null
            ? fmt.T(lang, "status.no_goal")
            : fmt.T(lang, "status.goal", goal.Name, fmt.Day(lang, goal.RaceDate), goal.GoalTimeSeconds is int g ? MessageFormatter.Time(g) : "–", fmt.RaceDistance(lang, goal.DistanceKm)));
        sb.AppendLine(plan is null ? fmt.T(lang, "status.no_plan") : fmt.T(lang, "status.plan", plan.Version, fmt.T(lang, $"plan.status.{plan.Status.ToString().ToLowerInvariant()}")));
        sb.AppendLine(fmt.T(lang, "status.profile", athlete.RunDaysPerWeek, MessageFormatter.Culture(lang).DateTimeFormat.GetDayName(athlete.LongRunDay),
            athlete.MaxHr?.ToString(CultureInfo.InvariantCulture) ?? "–"));
        sb.AppendLine(connection is not { IsConnected: true }
            ? fmt.T(lang, "status.coros_disconnected")
            : fmt.T(lang, "status.coros_connected", connection.LastSyncAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "–",
                connection.LastSyncError is null ? "OK" : connection.LastSyncError));
        sb.AppendLine(fmt.T(lang, "status.last_activity", lastActivity is null ? "–" : fmt.Day(lang, AthleteClock.LocalDate(lastActivity.Value, athlete.TimeZone))));
        if (pending > 0)
        {
            sb.AppendLine(fmt.T(lang, "status.pending", pending));
        }

        return new OutboundMessage(sb.ToString().TrimEnd());
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

        var sb = new StringBuilder();
        sb.AppendLine(fmt.T(lang, "daily.header", fmt.Day(lang, today)));
        AppendPlanState(sb, lang, plan);
        if (justMissed.Count > 0)
        {
            sb.AppendLine(fmt.T(lang, "daily.missed", justMissed.Count));
        }

        foreach (var w in plan.Workouts.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Rest && w.Date == today.AddDays(-1)))
        {
            sb.AppendLine(fmt.T(lang, "daily.unconfirmed", fmt.Line(lang, w)));
        }

        AppendWorkouts(sb, lang, plan, today, today, "report.today_header");
        sb.AppendLine();
        AppendReadiness(sb, lang, level, confidence, summary, concerns, "");
        sb.AppendLine().AppendLine(ai.Advice!.Trim());

        var buttons = new List<IReadOnlyList<MessageButton>>();
        foreach (var rec in recs)
        {
            var recMessage = recommendations.Render(athlete, rec);
            sb.AppendLine().AppendLine(rec.Summary).AppendLine(recMessage.Text);
            buttons.AddRange(recMessage.Buttons ?? []);
        }

        var lastRunWithoutRpe = await LastRunWithoutRpeAsync(athlete, today, ct);
        if (lastRunWithoutRpe is not null)
        {
            sb.AppendLine().AppendLine(fmt.T(lang, "daily.ask_rpe", MessageFormatter.Km(Math.Round(lastRunWithoutRpe.DistanceKm, 1))));
            buttons.Add([.. Enumerable.Range(1, 5).Select(i => new MessageButton($"{i}", $"rpe:{lastRunWithoutRpe.Id}:{i}"))]);
            buttons.Add([.. Enumerable.Range(6, 5).Select(i => new MessageButton($"{i}", $"rpe:{lastRunWithoutRpe.Id}:{i}"))]);
        }

        return new OutboundMessage(sb.ToString().TrimEnd(), buttons.Count > 0 ? buttons : null);
    }

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
        var sb = new StringBuilder();
        sb.AppendLine(fmt.T(lang, "weekly.header", fmt.Day(lang, monday), fmt.Day(lang, monday.AddDays(6))));
        sb.AppendLine(fmt.T(lang, "weekly.volume", F(stats.km), F(stats.planned_km), F(stats.last_week_km)));
        sb.AppendLine(fmt.T(lang, "weekly.sessions", stats.completed, stats.planned_sessions, stats.missed));
        sb.AppendLine(fmt.T(lang, "weekly.recovery", F(stats.hrv), F(stats.hrv_prev), F(stats.rhr), F(stats.rhr_prev), F(stats.sleep_h)));

        var planInputs = plan is null ? null : PlanSnapshot.Of(plan);
        var ai = await llm.AskAsync<WeeklyReportResponse>("weekly_report", athlete.Id, new
        {
            stats,
            phase = planned.FirstOrDefault()?.Phase,
            goal_distance_km = planInputs?.RaceKm,
            diagnosis = planInputs?.Assessment is { } a ? new { a.Limiters, a.Priorities } : null,
            readiness_last_7_days = (await readiness.RecentAsync(athlete.Id, today.AddDays(-6), today, ct)).Select(r => new { r.Date, r.Level }),
        }, lang, LlmSchemas.WeeklyReport, ct, r => string.IsNullOrWhiteSpace(r.Summary) ? ["summary_missing"] : []);
        sb.AppendLine().AppendLine(ai.Summary.Trim());
        if (!string.IsNullOrWhiteSpace(ai.FocusNextWeek))
        {
            sb.AppendLine("🎯 " + ai.FocusNextWeek.Trim());
        }

        if (plan is not null)
        {
            AppendWorkouts(sb, lang, plan, monday.AddDays(7), monday.AddDays(13), "weekly.next_week");
        }

        return new OutboundMessage(sb.ToString().TrimEnd());
    }

    private async Task<Activity?> LastRunWithoutRpeAsync(Athlete athlete, DateOnly today, CancellationToken ct)
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

    private void AppendPlanState(StringBuilder sb, string lang, TrainingPlan? plan)
    {
        if (plan is null)
        {
            sb.AppendLine(fmt.T(lang, "report.no_plan"));
        }
        else if (plan.Status == PlanStatus.Paused)
        {
            sb.AppendLine(fmt.T(lang, "report.paused"));
        }
    }

    private void AppendWorkouts(StringBuilder sb, string lang, TrainingPlan? plan, DateOnly from, DateOnly to, string headerKey)
    {
        if (plan is null)
        {
            return;
        }

        var items = plan.Workouts.Where(w => w.Date >= from && w.Date <= to).OrderBy(w => w.Date).ToList();
        sb.AppendLine().AppendLine(fmt.T(lang, headerKey));
        if (items.Count == 0)
        {
            sb.AppendLine(fmt.T(lang, "report.rest_day"));
            return;
        }

        foreach (var w in items)
        {
            sb.AppendLine("• " + fmt.Line(lang, w));
            if (from == to && !string.IsNullOrWhiteSpace(w.Description))
            {
                sb.AppendLine("  " + w.Description);
            }
        }
    }

    private void AppendReadiness(
        StringBuilder sb, string lang, ReadinessLevel level, Confidence confidence, string summary, IReadOnlyList<string> concerns, string checkedOn)
    {
        sb.AppendLine(fmt.ReadinessLine(lang, level, confidence) + checkedOn);
        sb.AppendLine(summary);
        foreach (var concern in concerns)
        {
            sb.AppendLine("• " + concern);
        }
    }
}
