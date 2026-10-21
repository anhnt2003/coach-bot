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
    ICorosGateway gateway,
    AlertService alerts,
    CoachLlm llm,
    MessageFormatter fmt,
    TimeProvider time)
{
    public async Task<OutboundMessage> TodayAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        var state = await readiness.EvaluateAsync(athlete, today, ct);

        var sb = new StringBuilder();
        AppendPlanState(sb, lang, plan);
        AppendWorkouts(sb, lang, plan, today, today, "report.today_header");
        AppendReadiness(sb, lang, state);
        return new OutboundMessage(sb.ToString().TrimEnd());
    }

    public async Task<OutboundMessage> WeekAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var monday = PlanGenerator.MondayOf(today);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);

        var sb = new StringBuilder();
        AppendPlanState(sb, lang, plan);
        AppendWorkouts(sb, lang, plan, monday, monday.AddDays(6), "report.week_header");
        return new OutboundMessage(sb.ToString().TrimEnd());
    }

    public async Task<OutboundMessage> StatusAsync(Athlete athlete, string corosMode, CancellationToken ct)
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
        sb.AppendLine(corosMode == "mock"
            ? fmt.T(lang, "status.coros_mock")
            : connection?.EncryptedRefreshToken is null && connection?.EncryptedAccessToken is null
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
    /// Morning check: readiness, today's session, fatigue alert, missed-workout bookkeeping and feedback nudges.
    /// Returns null when there is no plan and nothing worth sending.
    /// </summary>
    public async Task<OutboundMessage?> DailyCheckAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        var state = await readiness.EvaluateAsync(athlete, today, ct);

        var sb = new StringBuilder();
        sb.AppendLine(fmt.T(lang, "daily.header", fmt.Day(lang, today)));
        AppendPlanState(sb, lang, plan);

        if (plan is not null)
        {
            // Missed sessions are recorded, never made up: piling missed volume onto later days is an injury risk.
            var missed = plan.Workouts.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Rest && w.Date <= today.AddDays(-2)).ToList();
            foreach (var w in missed)
            {
                w.Status = WorkoutStatus.Missed;
                w.UpdatedAt = time.GetUtcNow();
            }

            await db.SaveChangesAsync(ct);
            if (missed.Count > 0)
            {
                sb.AppendLine(fmt.T(lang, "daily.missed", missed.Count));
            }

            var unconfirmed = plan.Workouts.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Rest && w.Date == today.AddDays(-1)).ToList();
            foreach (var w in unconfirmed)
            {
                sb.AppendLine(fmt.T(lang, "daily.unconfirmed", fmt.Line(lang, w)));
            }

            AppendWorkouts(sb, lang, plan, today, today, "report.today_header");
        }

        AppendReadiness(sb, lang, state);

        if (state.Today.Level == ReadinessLevel.Red)
        {
            await alerts.RaiseAsync(athlete.Id, AlertType.Fatigue, AlertSeverity.Warning, fmt.T(lang, "readiness.level.red"), $"fatigue:{today}", ct, send: false);
            sb.AppendLine(fmt.T(lang, "daily.red_advice"));
        }

        if (state.Today.Missing.Count >= 3)
        {
            await alerts.RaiseAsync(athlete.Id, AlertType.MissingData, AlertSeverity.Info, fmt.T(lang, "daily.missing_data"), $"missing:{today}", ct, send: false);
            sb.AppendLine(fmt.T(lang, "daily.missing_data"));
        }

        var buttons = new List<IReadOnlyList<MessageButton>>();
        var lastRunWithoutRpe = await LastRunWithoutRpeAsync(athlete, today, ct);
        if (lastRunWithoutRpe is not null)
        {
            sb.AppendLine().AppendLine(fmt.T(lang, "daily.ask_rpe", MessageFormatter.Km(Math.Round(lastRunWithoutRpe.DistanceKm, 1))));
            buttons.Add([.. Enumerable.Range(1, 5).Select(i => new MessageButton($"{i}", $"rpe:{lastRunWithoutRpe.Id}:{i}"))]);
            buttons.Add([.. Enumerable.Range(6, 5).Select(i => new MessageButton($"{i}", $"rpe:{lastRunWithoutRpe.Id}:{i}"))]);
        }

        if (plan is null && state.Today.Signals.Count == 0)
        {
            return null;
        }

        return new OutboundMessage(sb.ToString().TrimEnd(), buttons.Count > 0 ? buttons : null);
    }

    public async Task<OutboundMessage?> WeeklyReportAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var monday = PlanGenerator.MondayOf(today);
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

        var flags = await db.RunAnalyses.Where(r => r.AthleteId == athlete.Id && r.CreatedAt >= time.GetUtcNow().AddDays(-7)).Select(r => r.DetailsJson).ToListAsync(ct);
        var easyTooFast = flags.Count(f => f.Contains("easy_too_fast", StringComparison.Ordinal));
        if (easyTooFast >= 2)
        {
            sb.AppendLine(fmt.T(lang, "weekly.easy_too_fast", easyTooFast));
        }

        var planInputs = plan is null ? null : CoachJson.Deserialize<PlanSnapshot>(plan.InputsJson);
        var ai = await llm.AskAsync<WeeklyReportResponse>("weekly_report", new
        {
            stats,
            phase = planned.FirstOrDefault()?.Phase,
            goal_distance_km = planInputs?.Zones.RaceKm,
            diagnosis = planInputs?.Assessment is { } a ? new { a.Limiters, a.Priorities } : null,
        }, lang, LlmSchemas.WeeklyReport, ct);
        if (ai is not null)
        {
            sb.AppendLine().AppendLine(ai.Summary);
            if (!string.IsNullOrWhiteSpace(ai.FocusNextWeek))
            {
                sb.AppendLine("🎯 " + ai.FocusNextWeek);
            }
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

    private void AppendReadiness(StringBuilder sb, string lang, ReadinessHistory state)
    {
        sb.AppendLine().AppendLine(fmt.T(lang, $"readiness.level.{state.Today.Level.ToString().ToLowerInvariant()}")
                                   + " " + fmt.T(lang, $"readiness.confidence.{state.Today.Confidence.ToString().ToLowerInvariant()}"));
        foreach (var s in state.Today.Signals)
        {
            sb.AppendLine(fmt.SignalLine(lang, s));
        }

        if (state.Today.Missing.Count > 0)
        {
            sb.AppendLine(fmt.T(lang, "readiness.missing", string.Join(", ", state.Today.Missing.Select(m => fmt.T(lang, $"readiness.signal.{m}")))));
        }
    }
}
