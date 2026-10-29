using System.Text;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Analysis;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoachBot.Core.Services;

/// <summary>Post-run pipeline: match → compliance → readiness → adaptation (rules + validated LLM suggestions) → message.</summary>
public sealed class AnalysisService(
    ICoachDb db,
    PlanService plans,
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

        var matched = plan is null ? null : WorkoutMatcher.Match(runDate, activity.DistanceKm, plan.Workouts);
        if (matched is not null)
        {
            matched.Status = WorkoutStatus.Completed;
            matched.CompletedActivityId = activity.Id;
            matched.UpdatedAt = time.GetUtcNow();
        }

        var compliance = RunAnalyzer.Analyze(activity, matched);
        var state = await readiness.EvaluateAsync(athlete, today, ct);

        // Backfilled older runs are recorded but do not trigger adaptations or messages.
        var isRecent = runDate >= today.AddDays(-1);
        var canAdapt = isRecent && plan?.Status == PlanStatus.Active;

        var proposal = AdaptationProposal.None;
        if (canAdapt)
        {
            var snapshot = CoachJson.Deserialize<PlanSnapshot>(plan!.InputsJson);
            int? planThreshold = snapshot?.Zones.Basis == ZoneBasis.CorosThreshold ? snapshot.Zones.ThresholdPaceSecPerKm : null;
            var thresholdSince = today.AddDays(-14);
            var currentThreshold = await db.DailyMetrics
                .Where(m => m.AthleteId == athlete.Id && m.Source == gateway.Source && m.Date >= thresholdSince && m.ThresholdPaceSecPerKm != null)
                .OrderByDescending(m => m.Date).Select(m => m.ThresholdPaceSecPerKm).FirstOrDefaultAsync(ct);
            proposal = AdaptationEngine.Propose(new AdaptationContext(
                today, state.Today, state.Previous, plan.Workouts, planThreshold is null ? null : currentThreshold, planThreshold));
        }

        RunAnalysisResponse? ai = null;
        if (isRecent)
        {
            var latestNotes = await db.Feedback.Where(f => f.AthleteId == athlete.Id && f.Notes != null)
                .OrderByDescending(f => f.CreatedAt).Select(f => f.Notes).FirstOrDefaultAsync(ct);
            var planInputs = plan is null ? null : CoachJson.Deserialize<PlanSnapshot>(plan.InputsJson);
            ai = await llm.AskOrNullAsync<RunAnalysisResponse>("run_analysis", athlete.Id, new
            {
                goal_distance_km = planInputs?.Zones.RaceKm,
                diagnosis = planInputs?.Assessment is { } a ? new { a.Level, a.Limiters, a.Priorities } : null,
                run = new { date = runDate, km = Math.Round(activity.DistanceKm, 2), duration_min = activity.DurationSeconds / 60, pace = MessageFormatter.Pace(activity.AvgPaceSecPerKm), activity.AvgHr, activity.MaxHr, activity.TrainingLoad },
                planned = matched is null ? null : new { matched.Type, matched.Title, structure = matched.Description, km = matched.TargetDistanceKm, pace = new[] { MessageFormatter.Pace(matched.PaceMinSecPerKm), MessageFormatter.Pace(matched.PaceMaxSecPerKm) }, hr = new[] { matched.HrMin, matched.HrMax } },
                compliance,
                readiness = new { state.Today.Level, state.Today.Confidence, state.Today.Signals, state.Today.Missing, previous_days = state.Previous },
                rule_based_changes = proposal.Changes,
                upcoming = plan?.Workouts.Where(w => w.Status == WorkoutStatus.Planned && w.Date >= today && w.Date <= today.AddDays(7))
                    .OrderBy(w => w.Date).Select(w => new { workout_id = w.Id, w.Date, w.Type, w.Title, km = w.TargetDistanceKm, w.Phase }),
                athlete_notes = latestNotes,
            }, lang, LlmSchemas.RunAnalysis, ct);
        }

        var analysis = new RunAnalysis
        {
            AthleteId = athlete.Id,
            ActivityId = activity.Id,
            PlannedWorkoutId = matched?.Id,
            DistanceRatio = compliance.DistanceRatio,
            PaceStatus = compliance.PaceStatus,
            HrStatus = compliance.HrStatus,
            Readiness = state.Today.Level,
            ReadinessConfidence = state.Today.Confidence,
            DetailsJson = CoachJson.Serialize(new { compliance.Flags, state.Today.Signals, state.Today.Missing, rule_reason = proposal.ReasonKey }),
            Narrative = ai?.Summary,
            CreatedAt = time.GetUtcNow(),
        };
        db.RunAnalyses.Add(analysis);
        await db.SaveChangesAsync(ct);

        IReadOnlyList<Recommendation> recs = [];
        var llmChanges = ParseSuggestions(ai);
        if (canAdapt && (proposal.Changes.Count > 0 || llmChanges.Count > 0))
        {
            var reasonKey = proposal.IsEmpty ? "adapt.llm_easing" : proposal.ReasonKey;
            recs = await recommendations.CreateAsync(athlete, plan!, proposal.Changes, llmChanges, state.Today,
                proposal.IsEmpty ? "llm_easing" : proposal.ReasonKey, fmt.T(lang, reasonKey), analysis.Id, today, ct);
        }

        if (state.Today.Level != ReadinessLevel.Green)
        {
            await alerts.RaiseAsync(athlete.Id, AlertType.Fatigue,
                state.Today.Level == ReadinessLevel.Red ? AlertSeverity.Warning : AlertSeverity.Info,
                fmt.T(lang, $"readiness.level.{state.Today.Level.ToString().ToLowerInvariant()}"), $"fatigue:{today}", ct, send: false);
        }

        var concerns = ai?.SymptomConcerns?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? [];
        if (concerns.Count > 0)
        {
            // Notes are triaged (and can pause the plan) when the athlete sends them. Here the LLM only re-reads older
            // notes, so it raises a concern and asks the athlete to report via /symptom instead of pausing again.
            await alerts.RaiseAsync(athlete.Id, AlertType.Symptom, AlertSeverity.Warning, string.Join("; ", concerns),
                $"llm_symptom:{activity.Id}", ct, send: false);
        }

        if (isRecent)
        {
            await notifier.SendAsync(athlete.Id, Render(athlete, activity, matched, compliance, state, analysis, recs, concerns), ct);
        }

        return analysis;
    }

    private OutboundMessage Render(
        Athlete athlete, Activity activity, PlannedWorkout? matched, RunCompliance compliance, ReadinessHistory state,
        RunAnalysis analysis, IReadOnlyList<Recommendation> recs, IReadOnlyList<string> concerns)
    {
        var lang = athlete.Language;
        var sb = new StringBuilder();
        sb.AppendLine(fmt.T(lang, "analysis.header", MessageFormatter.Km(Math.Round(activity.DistanceKm, 1)),
            MessageFormatter.Pace(activity.AvgPaceSecPerKm), activity.AvgHr?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "–"));
        sb.AppendLine(matched is null ? fmt.T(lang, "analysis.unplanned") : fmt.T(lang, "analysis.planned", fmt.Line(lang, matched)));

        foreach (var flag in compliance.Flags.Where(f => f != "unplanned_run"))
        {
            sb.AppendLine("• " + fmt.T(lang, $"analysis.flag.{flag}"));
        }

        sb.AppendLine();
        sb.AppendLine(fmt.T(lang, $"readiness.level.{state.Today.Level.ToString().ToLowerInvariant()}")
                      + " " + fmt.T(lang, $"readiness.confidence.{state.Today.Confidence.ToString().ToLowerInvariant()}"));
        foreach (var signal in state.Today.Concerns)
        {
            sb.AppendLine(fmt.SignalLine(lang, signal));
        }

        if (state.Today.Missing.Count > 0)
        {
            sb.AppendLine(fmt.T(lang, "readiness.missing", string.Join(", ", state.Today.Missing.Select(m => fmt.T(lang, $"readiness.signal.{m}")))));
        }

        if (!string.IsNullOrWhiteSpace(analysis.Narrative))
        {
            sb.AppendLine().AppendLine(analysis.Narrative);
        }

        if (concerns.Count > 0)
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

    /// <summary>
    /// Turns the LLM's suggestions into candidate changes. Nothing here is trusted: unknown ids/actions are dropped, and
    /// AdaptationEngine.MergeLlmProposals rebuilds the "before" state from the database and applies the safety gates.
    /// </summary>
    private static List<ProposedChange> ParseSuggestions(RunAnalysisResponse? ai)
    {
        var result = new List<ProposedChange>();
        foreach (var s in ai?.Suggestions ?? [])
        {
            if (!Guid.TryParse(s.WorkoutId, out var id))
            {
                continue;
            }

            ChangeAction? action = s.Action switch
            {
                "reduce_distance" => ChangeAction.ReduceDistance,
                "downgrade" => ChangeAction.Downgrade,
                "convert_to_rest" => ChangeAction.ConvertToRest,
                "swap_days" => ChangeAction.SwapDays,
                "increase_distance" => ChangeAction.IncreaseDistance,
                "upgrade" => ChangeAction.Upgrade,
                _ => null,
            };
            var hasType = Enum.TryParse<WorkoutType>(s.ToType?.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var toType);
            DateOnly? newDate = DateOnly.TryParseExact(s.NewDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;
            if (action is null || (action is ChangeAction.Upgrade or ChangeAction.Downgrade && !hasType) || (action == ChangeAction.SwapDays && newDate is null))
            {
                continue;
            }

            result.Add(new ProposedChange(id, default, action.Value, WorkoutType.Rest, hasType ? toType : WorkoutType.Easy,
                0, action == ChangeAction.ConvertToRest ? 0 : Math.Max(0, s.ToKm), newDate));
        }

        return result;
    }
}
