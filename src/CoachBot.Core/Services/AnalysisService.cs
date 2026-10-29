using System.Text;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoachBot.Core.Services;

/// <summary>
/// After each run the LLM decides which planned session it was, how it went, how ready the athlete is, and how the
/// coming sessions should change. Code gathers the data, checks the answer can be stored, and writes it.
/// </summary>
public sealed class AnalysisService(
    ICoachDb db,
    PlanService plans,
    AthleteService athletes,
    ReadinessService readiness,
    RecommendationService recommendations,
    ICorosGateway gateway,
    AlertService alerts,
    CoachLlm llm,
    MessageFormatter fmt,
    INotifier notifier,
    TimeProvider time,
    ILogger<AnalysisService> logger)
{
    /// <summary>Runs this recent without an analysis are analyzed on every sync; older ones never are.</summary>
    public const int PendingWindowHours = 36;

    /// <summary>
    /// Analyzes every recent run that has no analysis yet, oldest first. A failed analysis writes nothing, so the run
    /// is picked up again by the next sync; the athlete is told once per run.
    /// </summary>
    /// <returns>The number of runs analyzed.</returns>
    public async Task<int> AnalyzePendingAsync(Athlete athlete, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddHours(-PendingWindowHours);
        var pending = await db.Activities
            .Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source && a.StartTime >= since
                        && Activity.RunSportTypes.Contains(a.SportType)
                        && !db.RunAnalyses.Any(r => r.ActivityId == a.Id))
            .OrderBy(a => a.StartTime)
            .ToListAsync(ct);

        var analyzed = 0;
        foreach (var activity in pending)
        {
            try
            {
                await AnalyzeRunAsync(athlete, activity, ct);
                analyzed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Analysis of activity {ActivityId} failed; retried on the next sync", activity.Id);
                var lang = athlete.Language;
                var reason = ex is LlmException llmFailure ? fmt.T(lang, $"llm.reason.{llmFailure.Reason.ToString().ToLowerInvariant()}") : ex.Message;
                await alerts.RaiseAsync(athlete.Id, AlertType.AnalysisFailure, AlertSeverity.Info,
                    fmt.T(lang, "analysis.failed_retrying", MessageFormatter.Km(Math.Round(activity.DistanceKm, 1)),
                        fmt.Day(lang, AthleteClock.LocalDate(activity.StartTime, athlete.TimeZone)), reason),
                    $"analysis_failed:{activity.Id}", ct);
            }
        }

        return analyzed;
    }

    /// <summary>
    /// Analyzes one run. Nothing is written until the LLM's answer passed the integrity gate, so when it fails
    /// (<see cref="LlmException"/>) the run stays unanalyzed and the next sync retries it.
    /// </summary>
    public async Task<RunAnalysis?> AnalyzeRunAsync(Athlete athlete, Activity activity, CancellationToken ct)
    {
        if (await db.RunAnalyses.AnyAsync(r => r.ActivityId == activity.Id, ct))
        {
            return null;
        }

        var lang = athlete.Language;
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var runDate = AthleteClock.LocalDate(activity.StartTime, athlete.TimeZone);
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        var goal = await athletes.GetActiveGoalAsync(athlete.Id, ct);
        var snapshot = plan is null ? null : PlanSnapshot.Of(plan);
        var paces = plan is null ? PaceTable.Empty(goal?.DistanceKm ?? RaceProfiles.MarathonKm) : PlanSnapshot.PacesOf(plan);
        var recovery = await readiness.LoadContextAsync(athlete, today, ct);

        var window = plan?.Workouts.Where(w => w.Date >= runDate.AddDays(-3) && w.Date <= runDate.AddDays(7)).OrderBy(w => w.Date).ToList() ?? [];
        var matchable = window.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Rest).Select(w => w.Id).ToHashSet();

        // Backfilled older runs are analyzed, but only a run from today or yesterday changes the plan and gets a message.
        var isRecent = runDate >= today.AddDays(-1);
        var canAdapt = isRecent && plan?.Status == PlanStatus.Active;
        var offered = canAdapt
            ? window.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Race && w.Date >= today).Select(w => w.Id).ToHashSet()
            : [];
        var raceDate = goal?.RaceDate ?? plan?.EndDate ?? today;

        var ai = await llm.AskAsync<RunAnalysisResponse>("run_analysis", athlete.Id, new
        {
            today,
            run = new
            {
                date = runDate,
                km = Math.Round(activity.DistanceKm, 2),
                duration_min = activity.DurationSeconds / 60,
                pace = MessageFormatter.Pace(activity.AvgPaceSecPerKm),
                avg_hr = activity.AvgHr,
                max_hr = activity.MaxHr,
                training_load = activity.TrainingLoad,
                activity.Name,
            },
            goal = LlmContext.Goal(goal),
            diagnosis = snapshot?.Assessment is { } a ? new { a.Level, a.Limiters, a.Priorities, a.Risks } : null,
            plan = plan is null ? null : new { status = plan.Status, paces = paces.Entries, sessions = window.Select(LlmContext.Session) },
            changes_allowed = canAdapt,
            daily_metrics = recovery.DailyMetrics,
            runs = recovery.Runs,
            feedback = recovery.Feedback,
            recent_readiness = recovery.RecentReadiness,
            athlete_notes = athlete.Notes,
        }, lang, LlmSchemas.RunAnalysis, ct, answer =>
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(answer.Summary))
            {
                errors.Add("summary_missing");
            }

            LlmGates.Readiness(answer.Readiness, errors);
            if (!LlmGates.PaceStatuses.Contains(answer.PaceStatus))
            {
                errors.Add($"pace_status_invalid: {answer.PaceStatus ?? "null"}");
            }

            if (!LlmGates.HrStatuses.Contains(answer.HrStatus))
            {
                errors.Add($"hr_status_invalid: {answer.HrStatus ?? "null"}");
            }

            var matched = MatchedId(answer);
            if (!string.IsNullOrWhiteSpace(answer.MatchedWorkoutId) && (matched is null || !matchable.Contains(matched.Value)))
            {
                errors.Add($"matched_workout_unknown: {answer.MatchedWorkoutId} is not a planned session you were given");
            }

            if (canAdapt)
            {
                // The session this run fulfilled is done; it cannot also be changed.
                var changeable = offered.Where(id => id != matched).ToHashSet();
                errors.AddRange(WorkoutChangeMapper.Map(answer.Changes, plan!, changeable, paces, today, raceDate).Errors);
            }

            return errors;
        });

        var (level, confidence) = LlmGates.Readiness(ai.Readiness, []);
        var readinessSummary = ai.Readiness!.Summary!.Trim();
        var concerns = Clean(ai.Readiness.Concerns);
        var symptomConcerns = Clean(ai.SymptomConcerns);
        var changes = canAdapt ? WorkoutChangeMapper.Map(ai.Changes, plan!, offered, paces, today, raceDate).Changes : [];

        var matched = MatchedId(ai) is Guid id ? plan!.Workouts.First(w => w.Id == id) : null;
        if (matched is not null)
        {
            matched.Status = WorkoutStatus.Completed;
            matched.CompletedActivityId = activity.Id;
            matched.UpdatedAt = time.GetUtcNow();
        }

        var analysis = new RunAnalysis
        {
            AthleteId = athlete.Id,
            ActivityId = activity.Id,
            PlannedWorkoutId = matched?.Id,
            DistanceRatio = matched is { TargetDistanceKm: > 0 } ? Math.Round(activity.DistanceKm / matched.TargetDistanceKm, 2) : null,
            PaceStatus = ai.PaceStatus!,
            HrStatus = ai.HrStatus!,
            Readiness = level,
            ReadinessConfidence = confidence,
            DetailsJson = CoachJson.Serialize(new { readiness_summary = readinessSummary, readiness_concerns = concerns, symptom_concerns = symptomConcerns }),
            Narrative = ai.Summary!.Trim(),
            CreatedAt = time.GetUtcNow(),
        };
        db.RunAnalyses.Add(analysis);
        if (isRecent)
        {
            await readiness.UpsertAsync(athlete.Id, today, level, confidence, readinessSummary, concerns, "run_analysis", ct);
        }

        await db.SaveChangesAsync(ct);

        IReadOnlyList<Recommendation> recs = changes.Count > 0
            ? await recommendations.CreateAsync(athlete, plan!, changes, "run_analysis", fmt.T(lang, "adapt.from_run"), analysis.Id, ct)
            : [];

        if (isRecent && level != ReadinessLevel.Green)
        {
            await alerts.RaiseAsync(athlete.Id, AlertType.Fatigue, level == ReadinessLevel.Red ? AlertSeverity.Warning : AlertSeverity.Info,
                readinessSummary, $"fatigue:{today:yyyy-MM-dd}", ct, send: false);
        }

        if (symptomConcerns.Count > 0)
        {
            // Notes are triaged (and can pause the plan) when the athlete sends them. Here the LLM only re-reads them,
            // so it raises a concern and asks the athlete to report via /symptom instead of pausing again.
            await alerts.RaiseAsync(athlete.Id, AlertType.Symptom, AlertSeverity.Warning, string.Join("; ", symptomConcerns),
                $"llm_symptom:{activity.Id}", ct, send: false);
        }

        if (isRecent)
        {
            await notifier.SendAsync(athlete.Id, Render(athlete, activity, matched, level, confidence, readinessSummary, concerns, analysis, recs, symptomConcerns), ct);
        }

        logger.LogInformation("Run {ActivityId} analyzed: matched {Matched}, readiness {Level}, {Changes} change(s)", activity.Id, matched?.Id, level, changes.Count);
        return analysis;
    }

    private static Guid? MatchedId(RunAnalysisResponse answer) => Guid.TryParse(answer.MatchedWorkoutId, out var id) ? id : null;

    private static List<string> Clean(IReadOnlyList<string>? items) => items?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList() ?? [];

    private OutboundMessage Render(
        Athlete athlete, Activity activity, PlannedWorkout? matched, ReadinessLevel level, Confidence confidence, string readinessSummary,
        IReadOnlyList<string> concerns, RunAnalysis analysis, IReadOnlyList<Recommendation> recs, IReadOnlyList<string> symptomConcerns)
    {
        var lang = athlete.Language;
        var sb = new StringBuilder();
        sb.AppendLine(fmt.T(lang, "analysis.header", MessageFormatter.Km(Math.Round(activity.DistanceKm, 1)),
            MessageFormatter.Pace(activity.AvgPaceSecPerKm), activity.AvgHr?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "–"));
        sb.AppendLine(matched is null ? fmt.T(lang, "analysis.unplanned") : fmt.T(lang, "analysis.planned", fmt.Line(lang, matched)));

        sb.AppendLine();
        sb.AppendLine(fmt.ReadinessLine(lang, level, confidence));
        sb.AppendLine(readinessSummary);
        foreach (var concern in concerns)
        {
            sb.AppendLine("• " + concern);
        }

        if (!string.IsNullOrWhiteSpace(analysis.Narrative))
        {
            sb.AppendLine().AppendLine(analysis.Narrative);
        }

        if (symptomConcerns.Count > 0)
        {
            sb.AppendLine().AppendLine(fmt.T(lang, "analysis.llm_concern"));
        }

        var buttons = new List<IReadOnlyList<MessageButton>>();
        foreach (var rec in recs)
        {
            var recMessage = recommendations.Render(athlete, rec);
            sb.AppendLine().AppendLine(rec.Summary).AppendLine(recMessage.Text);
            buttons.AddRange(recMessage.Buttons ?? []);
        }

        sb.AppendLine().AppendLine(fmt.T(lang, "analysis.ask_rpe"));
        buttons.Add([.. Enumerable.Range(1, 5).Select(i => new MessageButton(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"rpe:{activity.Id}:{i}"))]);
        buttons.Add([.. Enumerable.Range(6, 5).Select(i => new MessageButton(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"rpe:{activity.Id}:{i}"))]);
        return new OutboundMessage(sb.ToString().TrimEnd(), buttons);
    }
}
