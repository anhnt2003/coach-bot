using System.Text;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Core.Services;

public sealed record PlanGenerationResult(TrainingPlan? Plan, IReadOnlyList<string> Warnings, string? FailureKey);

public sealed class PlanService(
    ICoachDb db,
    AthleteService athletes,
    ICorosGateway gateway,
    CoachLlm llm,
    MessageFormatter fmt,
    IOptions<SafetyOptions> safety,
    TimeProvider time,
    ILogger<PlanService> logger)
{
    public Task<TrainingPlan?> GetCurrentPlanAsync(Guid athleteId, CancellationToken ct) =>
        db.TrainingPlans
            .Include(p => p.Workouts)
            .Where(p => p.AthleteId == athleteId && (p.Status == PlanStatus.Active || p.Status == PlanStatus.Paused))
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

    public async Task<PlanGenerationResult> GenerateAsync(Athlete athlete, CancellationToken ct)
    {
        var goal = await athletes.GetActiveGoalAsync(athlete.Id, ct);
        if (goal is null)
        {
            return new(null, [], "plan.no_goal");
        }

        var today = AthleteClock.Today(time, athlete.TimeZone);
        var history = await LoadHistoryAsync(athlete, today, ct);
        var zones = PaceZoneCalculator.Calculate(new FitnessInputs(
            history.ThresholdPace, history.MarathonPrediction, history.Efforts, goal.GoalTimeSeconds, goal.DistanceKm,
            athlete.MaxHr, athlete.BirthYear, today.Year));

        var options = safety.Value.ForRace(goal.DistanceKm);
        var draft = new PlanGenerator(options).Generate(new PlanInputs(
            today, goal.RaceDate, goal.DistanceKm, history.WeeklyKmAvg, history.LongestRunKm, history.WeeksOfHistory,
            athlete.RunDaysPerWeek, athlete.LongRunDay, zones.Zones));

        var warnings = draft.Warnings.Concat(zones.Warnings).Distinct().ToList();
        if (!draft.Feasible)
        {
            return new(null, warnings, warnings.LastOrDefault(w => w.StartsWith("plan.infeasible", StringComparison.Ordinal)));
        }

        var validator = new PlanValidator(options);
        var constraints = new PlanConstraints(goal.RaceDate, Math.Clamp(athlete.RunDaysPerWeek, 3, 6));
        var violations = validator.ValidatePlan(draft.Workouts, draft.Baseline, zones.Zones, constraints);
        if (violations.Count > 0)
        {
            // The generator is tested to never do this; refuse rather than store an unsafe plan.
            logger.LogError("Generated plan failed validation: {Violations}", string.Join("; ", violations.Select(v => v.Code)));
            return new(null, warnings, "plan.internal_validation_failed");
        }

        var lang = athlete.Language;
        IReadOnlyList<PlannedWorkout> workouts = draft.Workouts;
        string? rationale = null;
        IReadOnlyList<WeeklyFocus>? focus = null;
        AthleteAssessment? assessment = null;
        var generatedBy = "rules";

        if (llm.IsEnabled)
        {
            var notesSince = time.GetUtcNow().AddDays(-30);
            var notes = await db.Feedback.Where(f => f.AthleteId == athlete.Id && f.Notes != null && f.CreatedAt >= notesSince)
                .OrderByDescending(f => f.CreatedAt).Select(f => f.Notes!).Take(10).ToListAsync(ct);
            assessment = await AssessAsync(athlete, goal, history, zones.Zones, draft, notes, warnings, ct);
            var designed = await DesignWithLlmAsync(athlete, goal, history, zones.Zones, draft, constraints, validator, options, assessment, notes, warnings, ct);
            if (designed is not null)
            {
                (workouts, rationale, focus) = (designed.Workouts, designed.Rationale, designed.Focus);
                warnings.AddRange(designed.Warnings.Except(warnings));
                generatedBy = $"llm:{llm.ProviderName}";
            }
            else
            {
                warnings.Add("plan.llm_rejected");
                var explained = await ExplainRulePlanAsync(goal, history, zones.Zones, draft, warnings, lang, ct);
                (rationale, focus) = (explained?.Rationale, explained?.WeeklyFocus);
            }
        }

        foreach (var w in workouts)
        {
            // An LLM-designed session carries its own title and coaching note; keep them in front of the targets.
            var note = w.Description;
            var title = w.Title;
            fmt.FillTexts(lang, w);
            if (!string.IsNullOrWhiteSpace(title))
            {
                w.Title = $"{title} · {MessageFormatter.Km(w.TargetDistanceKm)} km";
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                w.Description = w.StepsJson is null ? $"{note} {fmt.Targets(lang, w)}" : $"{note}\n{w.Description}";
            }
        }

        var snapshot = new PlanSnapshot(zones.Zones, draft.Baseline, history.WeeklyKmAvg, history.LongestRunKm, history.WeeksOfHistory, history.Vo2Max,
            Assessment: assessment);

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
            StartDate = draft.StartDate,
            EndDate = draft.EndDate,
            GeneratedBy = generatedBy,
            Rationale = rationale,
            WarningsJson = CoachJson.Serialize(warnings),
            InputsJson = CoachJson.Serialize(snapshot with { WeeklyFocus = focus }),
            CreatedAt = time.GetUtcNow(),
        };
        foreach (var w in workouts)
        {
            w.PlanId = plan.Id;
            w.UpdatedAt = time.GetUtcNow();
            plan.Workouts.Add(w);
        }

        db.TrainingPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Plan v{Version} generated by {GeneratedBy} ({Workouts} workouts, {Weeks} weeks)", plan.Version, generatedBy,
            plan.Workouts.Count, plan.Workouts.Max(w => w.WeekIndex) + 1);
        return new(plan, warnings, null);
    }

    private sealed record DesignedPlan(
        IReadOnlyList<PlannedWorkout> Workouts, string? Rationale, IReadOnlyList<WeeklyFocus> Focus, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Asks the LLM to design the plan, using the rule-based draft as a safe reference. Only the validator's enforced
    /// limits (data integrity, injury ceilings) can stop a design; guidelines it goes beyond become warnings. On enforced
    /// violations the LLM gets one chance to repair it, and whatever still breaks a limit after that is clamped week by
    /// week. Returns null when no design was produced or it could not be clamped (the caller keeps the rule-based plan).
    /// </summary>
    private async Task<DesignedPlan?> DesignWithLlmAsync(
        Athlete athlete, RaceGoal goal, History history, TrainingZones zones, PlanDraft draft, PlanConstraints constraints,
        PlanValidator validator, SafetyOptions o, AthleteAssessment? assessment, IReadOnlyList<string> notes, IReadOnlyList<string> warnings,
        CancellationToken ct)
    {
        var context = new
        {
            race = new
            {
                goal.Name,
                date = goal.RaceDate,
                distance_km = goal.DistanceKm,
                goal_time_seconds = goal.GoalTimeSeconds,
                predicted_time_seconds_from_current_fitness = PaceZoneCalculator.PredictedRaceSeconds(zones),
            },
            assessment,
            athlete = new
            {
                run_days_per_week = constraints.RunDaysPerWeek,
                long_run_day = athlete.LongRunDay.ToString().ToLowerInvariant()[..3],
                profile_notes = athlete.Notes,
                recent_notes = notes,
            },
            fitness = new
            {
                weekly_km_last_4_weeks_avg = history.WeeklyKmAvg,
                weekly_km_history_oldest_first = history.WeeklyKm,
                longest_run_last_4_weeks_km = history.LongestRunKm,
                vo2max = history.Vo2Max,
                zones_basis = zones.Basis,
                warnings,
            },
            plan_window = new
            {
                first_monday = draft.StartDate,
                weeks = draft.Workouts.Max(w => w.WeekIndex) + 1,
                race_week = draft.Workouts.Max(w => w.WeekIndex) + 1,
            },
            // Enforced: a design that breaks these is repaired, then clamped.
            limits = new
            {
                weekly_increase_max = $"min({o.MaxWeeklyIncreasePct:P0}, +{o.MaxWeeklyIncreaseKm} km) over the previous full (non-cutback, non-taper) week; week 1 at most {draft.Baseline.WeeklyKm} km",
                cutback = "a week marked cutback=true must not be bigger than the previous full week",
                long_run = $"<= {o.LongRunCapKm} km and <= {o.LongRunCapMinutes} min, +{o.LongRunMaxIncreaseKm} km/week max; week 1 at most {draft.Baseline.LongRunKm} km",
                taper = $"taper weeks marked phase=taper (the baseline's are a safe choice), each lower than the previous and <= 85% of peak; race week <= {o.RaceWeekMaxShare:P0} of peak"
                    + (draft.Workouts.Max(w => w.WeekIndex) >= 7 ? $", the week before it <= {o.PreRaceWeekMaxShare:P0} of peak" : ""),
                easy_sessions = "recovery and easy runs contain only recovery/easy running, plus strides of <= 30 s or 200 m on easy runs",
                steps = $"repeat 1..{WorkoutStructure.MaxRepeat}; recoveries at recovery, easy or steady; the steps make up the session distance",
                run_days = $"at most {constraints.RunDaysPerWeek} run days per week, one session per day",
                min_run_km = PlanValidator.MinRunKm,
                race = $"one session type=race on {goal.RaceDate:yyyy-MM-dd} ({goal.RaceDate.DayOfWeek}), nothing after it",
            },
            // Advisory: sound defaults from coaching practice. Going beyond one is allowed when it serves this athlete.
            guidelines = new
            {
                cutback_rhythm = $"every {o.CutbackEveryWeeks}th week about {1 - o.CutbackFactor:P0} lower",
                long_run_share = $"<= {o.LongRunHardMaxShare:P0} of the week",
                quality = $"tempo/interval/race_pace: <= {o.MaxQualityPerWeekBase} per week in base phase, <= {o.MaxQualityPerWeek} otherwise",
                easy_share = $">= {o.MinEasyShare:P0} of weekly km easy. Sessions with steps count their steps (steady 50%, marathon pace and faster 100%); without steps tempo counts 50% hard, interval 40%, race_pace 60%",
                intensity_per_session = new
                {
                    threshold_and_ten_k = Cap(o.ThresholdCap),
                    five_k_and_hill = Cap(o.Vo2Cap),
                    repetition_and_strides = Cap(o.RepetitionCap),
                    marathon_and_half_marathon = Cap(o.RacePaceCap),
                },
                session_intensities = new
                {
                    @long = "recovery, easy, steady, marathon, half_marathon",
                    tempo = "half_marathon, threshold or ten_k work; may add steady/marathon and strides",
                    interval = "ten_k, five_k, repetition or hill work",
                    race_pace = $"work at {PlanDesignMapper.IntensityName(zones.RaceIntensity)} (the goal race pace)",
                    strides = $"at most {WorkoutStructure.MaxStrides} per session",
                },
                spacing = "no hard days back-to-back (long, tempo, interval, race_pace, race), also across weeks; no hard session in the 2 days before the race",
            },
            baseline_plan = PlanDesignMapper.ToDesign(draft.Workouts),
        };

        var design = await llm.AskAsync<PlanDesign>("plan_design", context, athlete.Language, LlmSchemas.PlanDesign, ct);
        if (design is null)
        {
            return null;
        }

        (List<PlannedWorkout> Workouts, List<Violation> Errors, List<Violation> Violations) Check(PlanDesign d)
        {
            var (mapped, errors) = PlanDesignMapper.Map(d, draft.StartDate, goal.RaceDate, goal.DistanceKm, zones);
            return (mapped, errors, [.. errors, .. validator.ValidatePlan(mapped, draft.Baseline, zones, constraints)]);
        }

        static string Describe(IEnumerable<Violation> violations) =>
            string.Join("; ", violations.Take(15).Select(v => $"{v.Code} w{v.WeekIndex + 1} {v.Detail}"));

        var (workouts, designErrors, violations) = Check(design);
        if (violations.Any(v => !v.IsGuideline))
        {
            logger.LogWarning("LLM plan design broke enforced limits: {Violations}", Describe(violations.Where(v => !v.IsGuideline)));
            var repaired = await llm.AskAsync<PlanDesign>("plan_design_repair", new
            {
                previous_design = design,
                violations = violations.Where(v => !v.IsGuideline).Select(v => new { v.Code, week = v.WeekIndex + 1, v.Date, v.Detail }),
                guidelines_exceeded = violations.Where(v => v.IsGuideline).Select(v => new { v.Code, week = v.WeekIndex + 1, v.Date, v.Detail }),
                context,
            }, athlete.Language, LlmSchemas.PlanDesign, ct);
            if (repaired is not null)
            {
                design = repaired;
                (workouts, designErrors, violations) = Check(repaired);
            }
        }

        IReadOnlyList<int> clamped = [], replaced = [];
        if (violations.Any(v => !v.IsGuideline))
        {
            var result = PlanClamp.Apply(workouts, designErrors, draft, validator, zones, constraints);
            if (result is null)
            {
                logger.LogWarning("LLM plan design could not be clamped to the limits: {Violations}", Describe(violations.Where(v => !v.IsGuideline)));
                return null;
            }

            (workouts, clamped, replaced) = (result.Workouts, result.ClampedWeeks, result.ReplacedWeeks);
            violations = [.. validator.ValidatePlan(workouts, draft.Baseline, zones, constraints)];
            logger.LogInformation("LLM plan design clamped: weeks {Clamped} cut to the limits, weeks {Replaced} replaced by the baseline",
                string.Join(",", clamped.Select(w => w + 1)), string.Join(",", replaced.Select(w => w + 1)));
        }

        var added = violations.Select(v => PlanValidator.GuidelineGroup(v.Code)).OfType<string>().Distinct().Select(g => $"plan.guideline.{g}").ToList();
        if (added.Count > 0)
        {
            logger.LogInformation("LLM plan design goes beyond guidelines: {Violations}", Describe(violations));
        }

        if (clamped.Count > 0)
        {
            added.Add("plan.llm_clamped");
        }

        if (replaced.Count > 0)
        {
            added.Add("plan.llm_weeks_replaced");
        }

        var focus = (design.Weeks ?? [])
            .Where(w => !string.IsNullOrWhiteSpace(w.Focus) && !replaced.Contains(w.Week - 1))
            .Select(w => new WeeklyFocus(w.Week, w.Focus!))
            .ToList();
        return new DesignedPlan(workouts, design.Rationale, focus, added);
    }

    private static string Cap(IntensityCap cap) => $"<= {cap.Share:P0} of the week's km per session (at least {cap.FloorKm} km allowed, never more than {cap.MaxKm} km)";

    /// <summary>
    /// The LLM's diagnosis: level, goal realism, limiters and what each phase should develop. It is advice for the plan
    /// design and the athlete; it never changes a number by itself. Null when the LLM is unavailable.
    /// </summary>
    private Task<AthleteAssessment?> AssessAsync(
        Athlete athlete, RaceGoal goal, History history, TrainingZones zones, PlanDraft draft, IReadOnlyList<string> notes,
        IReadOnlyList<string> warnings, CancellationToken ct) =>
        llm.AskAsync<AthleteAssessment>("athlete_assessment", new
        {
            race = new
            {
                goal.Name,
                date = goal.RaceDate,
                distance_km = goal.DistanceKm,
                weeks_to_race = draft.Workouts.Max(w => w.WeekIndex) + 1,
                goal_time_seconds = goal.GoalTimeSeconds,
                predicted_time_seconds_from_current_fitness = PaceZoneCalculator.PredictedRaceSeconds(zones),
            },
            athlete = new
            {
                run_days_per_week = athlete.RunDaysPerWeek,
                age = athlete.BirthYear is int y ? AthleteClock.Today(time, athlete.TimeZone).Year - y : (int?)null,
                max_hr = athlete.MaxHr,
                profile_notes = athlete.Notes,
                recent_notes = notes,
            },
            fitness = new
            {
                weekly_km_history_oldest_first = history.WeeklyKm,
                weekly_km_last_4_weeks_avg = history.WeeklyKmAvg,
                longest_run_last_4_weeks_km = history.LongestRunKm,
                weeks_of_history = history.WeeksOfHistory,
                recent_runs = history.RecentRuns,
                vo2max = history.Vo2Max,
                threshold_pace_sec_per_km = zones.ThresholdPaceSecPerKm,
                zones_basis = zones.Basis,
                warnings,
            },
        }, athlete.Language, LlmSchemas.AthleteAssessment, ct);

    private Task<PlanRationaleResponse?> ExplainRulePlanAsync(
        RaceGoal goal, History history, TrainingZones zones, PlanDraft draft, IReadOnlyList<string> warnings, string lang, CancellationToken ct) =>
        llm.AskAsync<PlanRationaleResponse>("plan_rationale", new
        {
            race = new { goal.Name, goal.RaceDate, goal.DistanceKm, goal_time = goal.GoalTimeSeconds },
            current_fitness = new { weekly_km = history.WeeklyKmAvg, longest_run_km = history.LongestRunKm, weeks_of_history = history.WeeksOfHistory, history.Vo2Max, zones },
            warnings,
            weeks = draft.Workouts.GroupBy(w => w.WeekIndex).Select(g => new
            {
                week = g.Key + 1,
                phase = g.First().Phase,
                cutback = g.First().IsCutbackWeek,
                km = g.Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm),
                sessions = g.Where(w => w.Type != WorkoutType.Easy && w.Type != WorkoutType.Recovery).Select(w => $"{w.Type} {w.TargetDistanceKm}km"),
            }),
        }, lang, LlmSchemas.PlanRationale, ct);

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

    public OutboundMessage RenderProposal(Athlete athlete, TrainingPlan plan, RaceGoal goal, IReadOnlyList<string> warnings)
    {
        var lang = athlete.Language;
        var sb = new StringBuilder();
        var weeks = plan.Workouts.GroupBy(w => w.WeekIndex).OrderBy(g => g.Key).ToList();
        var peak = weeks.Max(g => g.Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm));
        var longest = plan.Workouts.Where(w => w.Type == WorkoutType.Long).Select(w => w.TargetDistanceKm).DefaultIfEmpty(0).Max();
        var snapshot = CoachJson.Deserialize<PlanSnapshot>(plan.InputsJson);

        var taperWeeks = weeks.Count(g => g.Any(w => w.Phase == PlanPhase.Taper));
        sb.AppendLine(fmt.T(lang, "plan.proposal_header", goal.Name, fmt.RaceDistance(lang, goal.DistanceKm), fmt.Day(lang, goal.RaceDate), weeks.Count));
        sb.AppendLine(fmt.T(lang, "plan.proposal_stats", MessageFormatter.Km(weeks[0].Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm)),
            MessageFormatter.Km(peak), MessageFormatter.Km(longest), taperWeeks));
        sb.AppendLine(fmt.T(lang, plan.GeneratedBy.StartsWith("llm", StringComparison.Ordinal) ? "plan.designed_by_llm" : "plan.designed_by_rules"));
        if (snapshot is not null)
        {
            sb.AppendLine(fmt.T(lang, $"zones.basis.{snapshot.Zones.Basis}"));
            if (PaceZoneCalculator.PredictedRaceSeconds(snapshot.Zones) is int predicted)
            {
                sb.AppendLine(goal.GoalTimeSeconds is int goalSeconds
                    ? fmt.T(lang, "plan.prediction_goal", MessageFormatter.Time(predicted), MessageFormatter.Time(goalSeconds))
                    : fmt.T(lang, "plan.prediction", MessageFormatter.Time(predicted)));
            }

            if (snapshot.Assessment is { } a)
            {
                sb.AppendLine().Append(RenderAssessment(lang, a));
            }
        }

        foreach (var warning in warnings)
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

    private sealed record History(
        double WeeklyKmAvg, double LongestRunKm, int WeeksOfHistory, IReadOnlyList<RecentEffort> Efforts,
        int? ThresholdPace, int? MarathonPrediction, double? Vo2Max, IReadOnlyList<double> WeeklyKm, IReadOnlyList<RecentRun> RecentRuns);

    /// <summary>A recent run as shown to the LLM for the diagnosis.</summary>
    private sealed record RecentRun(DateOnly Date, double Km, string? Pace, int? AvgHr, string? Name);

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
        var weeklyAvg = Math.Round(last4.Sum(r => r.DistanceKm) / divisor, 1);
        var longest = last4.Select(r => r.DistanceKm).DefaultIfEmpty(0).Max();

        var fitnessSince = today.AddDays(-30);
        var fitness = await db.DailyMetrics
            .Where(m => m.AthleteId == athlete.Id && m.Source == gateway.Source && m.Date >= fitnessSince && (m.ThresholdPaceSecPerKm != null || m.MarathonPredictionSeconds != null || m.Vo2Max != null))
            .OrderByDescending(m => m.Date)
            .FirstOrDefaultAsync(ct);

        var weeklyKm = Enumerable.Range(1, 8).Reverse()
            .Select(i => Math.Round(runs.Where(r => r.Date >= today.AddDays(-7 * i) && r.Date < today.AddDays(-7 * (i - 1))).Sum(r => r.DistanceKm), 1))
            .ToList();
        return new History(weeklyAvg, Math.Round(longest, 1), weeksOfHistory,
            [.. runs.Select(r => new RecentEffort(r.DistanceKm, r.DurationSeconds))],
            fitness?.ThresholdPaceSecPerKm, fitness?.MarathonPredictionSeconds, fitness?.Vo2Max, weeklyKm,
            [.. runs.Where(r => r.Date >= today.AddDays(-28)).OrderByDescending(r => r.Date).Take(20)
                .Select(r => new RecentRun(r.Date, Math.Round(r.DistanceKm, 1), r.AvgPaceSecPerKm is int p ? MessageFormatter.Pace(p) : null, r.AvgHr, r.Name))]);
    }
}
