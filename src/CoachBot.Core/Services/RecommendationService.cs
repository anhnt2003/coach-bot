using CoachBot.Core.Abstractions;
using CoachBot.Core.Analysis;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Core.Services;

public enum DecisionOutcome
{
    Done,
    NotFound,
    AlreadyDecided,
    Expired,
    NoLongerValid,
}

/// <summary>
/// Owns every change to an approved plan: validation, minor/major classification, applying, undo and expiry.
/// All changes are written to the <see cref="PlanChange"/> audit trail.
/// </summary>
public sealed class RecommendationService(
    ICoachDb db,
    MessageFormatter fmt,
    IOptions<SafetyOptions> safety,
    TimeProvider time,
    ILogger<RecommendationService> logger)
{
    private readonly ChangeClassifier _classifier = new(safety.Value);

    /// <summary>
    /// Validates and stores the changes coming out of a run analysis. Up to two recommendations are created:
    /// easing changes (rules + LLM reductions/swaps; minor ones are applied immediately) and LLM progression
    /// (always major, so it waits for the athlete's approval).
    /// </summary>
    public async Task<IReadOnlyList<Recommendation>> CreateAsync(
        Athlete athlete, TrainingPlan plan, IReadOnlyList<ProposedChange> ruleChanges, IReadOnlyList<ProposedChange> llmChanges,
        ReadinessAssessment? readiness, string trigger, string summary, Guid? analysisId, DateOnly today, CancellationToken ct)
    {
        var zones = Zones(plan);
        var byId = plan.Workouts.ToDictionary(w => w.Id);
        var (easing, progression) = AdaptationEngine.MergeLlmProposals(ruleChanges, llmChanges, byId, today, readiness);
        var created = new List<Recommendation>();

        if (!IsValid(plan, easing, zones, out var violations))
        {
            logger.LogWarning("Merged easing changes rejected by validator ({Violations}); using rule-based changes only",
                string.Join(", ", violations.Select(v => v.Code)));
            easing = IsValid(plan, ruleChanges, zones, out violations) ? ruleChanges : [];
            if (easing.Count == 0 && ruleChanges.Count > 0)
            {
                logger.LogWarning("Rule-based changes rejected by validator: {Violations}", string.Join(", ", violations.Select(v => v.Code)));
            }
        }

        if (easing.Count > 0)
        {
            created.Add(Store(athlete, plan, easing, trigger, summary, analysisId, today, zones));
        }

        if (progression.Count > 0)
        {
            if (IsValid(plan, progression, zones, out violations))
            {
                created.Add(Store(athlete, plan, progression, "llm_progression", fmt.T(athlete.Language, "adapt.llm_progression"), analysisId, today, zones));
            }
            else
            {
                logger.LogInformation("LLM progression proposal rejected by validator: {Violations}", string.Join(", ", violations.Select(v => v.Code)));
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var rec in created)
        {
            logger.LogInformation("Recommendation {Id} ({Trigger}, {Magnitude}, {Count} changes) {Status}", rec.Id, rec.Trigger, rec.Magnitude,
                CoachJson.Deserialize<List<ProposedChange>>(rec.ChangesJson)?.Count, rec.Status);
        }

        return created;
    }

    private Recommendation Store(
        Athlete athlete, TrainingPlan plan, IReadOnlyList<ProposedChange> changes, string trigger, string summary, Guid? analysisId,
        DateOnly today, TrainingZones zones)
    {
        var magnitude = _classifier.Classify(changes, today, plan.Workouts.ToDictionary(w => w.Id));
        var now = time.GetUtcNow();
        var rec = new Recommendation
        {
            AthleteId = athlete.Id,
            PlanId = plan.Id,
            RunAnalysisId = analysisId,
            Trigger = trigger,
            Summary = summary,
            ChangesJson = CoachJson.Serialize(changes),
            Magnitude = magnitude,
            Status = magnitude == ChangeMagnitude.Minor ? RecommendationStatus.AutoApplied : RecommendationStatus.Proposed,
            CreatedAt = now,
            ExpiresAt = now.AddHours(safety.Value.ApprovalExpiryHours),
        };
        db.Recommendations.Add(rec);

        if (magnitude == ChangeMagnitude.Minor)
        {
            Apply(athlete, plan, rec, changes, zones, "auto");
            rec.DecidedAt = now;
        }

        return rec;
    }

    public async Task<(DecisionOutcome Outcome, Recommendation? Rec)> ApproveAsync(Athlete athlete, Guid recId, CancellationToken ct)
    {
        var rec = await db.Recommendations.FirstOrDefaultAsync(r => r.Id == recId && r.AthleteId == athlete.Id, ct);
        if (rec is null)
        {
            return (DecisionOutcome.NotFound, null);
        }

        if (rec.Status != RecommendationStatus.Proposed)
        {
            return (DecisionOutcome.AlreadyDecided, rec);
        }

        var now = time.GetUtcNow();
        if (rec.ExpiresAt < now)
        {
            rec.Status = RecommendationStatus.Expired;
            await db.SaveChangesAsync(ct);
            return (DecisionOutcome.Expired, rec);
        }

        var plan = await db.TrainingPlans.Include(p => p.Workouts).FirstAsync(p => p.Id == rec.PlanId, ct);
        var changes = CoachJson.Deserialize<List<ProposedChange>>(rec.ChangesJson) ?? [];
        var zones = Zones(plan);

        // The plan may have changed since the proposal (other changes, completed workouts): re-check before applying.
        var stillPlanned = changes.All(c => plan.Workouts.Any(w => w.Id == c.WorkoutId && w.Status == WorkoutStatus.Planned));
        var increases = changes.Any(IsIncrease);
        if (!stillPlanned || plan.Status is not (PlanStatus.Active or PlanStatus.Paused) || (increases && plan.Status == PlanStatus.Paused)
            || !IsValid(plan, changes, zones, out _))
        {
            rec.Status = RecommendationStatus.Expired;
            rec.DecidedAt = now;
            await db.SaveChangesAsync(ct);
            return (DecisionOutcome.NoLongerValid, rec);
        }

        Apply(athlete, plan, rec, changes, zones, "athlete");
        rec.Status = RecommendationStatus.Approved;
        rec.DecidedAt = now;
        await db.SaveChangesAsync(ct);
        return (DecisionOutcome.Done, rec);
    }

    public async Task<DecisionOutcome> RejectAsync(Athlete athlete, Guid recId, CancellationToken ct)
    {
        var rec = await db.Recommendations.FirstOrDefaultAsync(r => r.Id == recId && r.AthleteId == athlete.Id, ct);
        if (rec is null)
        {
            return DecisionOutcome.NotFound;
        }

        if (rec.Status != RecommendationStatus.Proposed)
        {
            return DecisionOutcome.AlreadyDecided;
        }

        rec.Status = RecommendationStatus.Rejected;
        rec.DecidedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return DecisionOutcome.Done;
    }

    /// <summary>Reverts an auto-applied (minor) change using the audit trail snapshots.</summary>
    public async Task<DecisionOutcome> UndoAsync(Athlete athlete, Guid recId, CancellationToken ct)
    {
        var rec = await db.Recommendations.FirstOrDefaultAsync(r => r.Id == recId && r.AthleteId == athlete.Id, ct);
        if (rec is null)
        {
            return DecisionOutcome.NotFound;
        }

        if (rec.Status != RecommendationStatus.AutoApplied)
        {
            return DecisionOutcome.AlreadyDecided;
        }

        var changes = await db.PlanChanges.Where(c => c.RecommendationId == rec.Id).OrderByDescending(c => c.AppliedAt).ToListAsync(ct);
        var workoutIds = changes.Select(c => c.PlannedWorkoutId).ToList();
        var workouts = await db.PlannedWorkouts.Where(w => workoutIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        if (workouts.Values.Any(w => w.Status != WorkoutStatus.Planned))
        {
            return DecisionOutcome.NoLongerValid;
        }

        var now = time.GetUtcNow();
        foreach (var change in changes)
        {
            var workout = workouts[change.PlannedWorkoutId];
            var before = CoachJson.Deserialize<WorkoutState>(change.BeforeJson);
            if (before is null)
            {
                continue;
            }

            var current = PlanAdjuster.Snapshot(workout);
            PlanAdjuster.Restore(workout, before);
            workout.UpdatedAt = now;
            db.PlanChanges.Add(new PlanChange
            {
                PlanId = change.PlanId,
                PlannedWorkoutId = workout.Id,
                RecommendationId = rec.Id,
                BeforeJson = current,
                AfterJson = change.BeforeJson,
                Reason = "undo",
                AppliedBy = "athlete",
                AppliedAt = now,
            });
        }

        rec.Status = RecommendationStatus.Undone;
        rec.DecidedAt = now;
        await db.SaveChangesAsync(ct);
        return DecisionOutcome.Done;
    }

    /// <summary>Major proposals that were not answered in time expire; the plan stays as it was.</summary>
    public async Task<IReadOnlyList<Recommendation>> ExpireDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await db.Recommendations.Where(r => r.Status == RecommendationStatus.Proposed && r.ExpiresAt < now).ToListAsync(ct);
        foreach (var rec in due)
        {
            rec.Status = RecommendationStatus.Expired;
            rec.DecidedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return due;
    }

    public OutboundMessage Render(Athlete athlete, Recommendation rec)
    {
        var lang = athlete.Language;
        var changes = CoachJson.Deserialize<List<ProposedChange>>(rec.ChangesJson) ?? [];
        var lines = string.Join("\n", changes.Select(c => "• " + fmt.ChangeLine(lang, c)));
        if (rec.Status == RecommendationStatus.AutoApplied)
        {
            return new OutboundMessage($"{fmt.T(lang, "rec.auto_applied_header")}\n{lines}",
                [[new MessageButton(fmt.T(lang, "button.undo"), $"rec:undo:{rec.Id}")]]);
        }

        return new OutboundMessage($"{fmt.T(lang, "rec.proposed_header")}\n{lines}\n{fmt.T(lang, "rec.expires_hint", safety.Value.ApprovalExpiryHours)}",
            [[new MessageButton(fmt.T(lang, "button.approve"), $"rec:approve:{rec.Id}"), new MessageButton(fmt.T(lang, "button.reject"), $"rec:reject:{rec.Id}")]]);
    }

    private void Apply(Athlete athlete, TrainingPlan plan, Recommendation rec, IReadOnlyList<ProposedChange> changes, TrainingZones zones, string appliedBy)
    {
        var now = time.GetUtcNow();
        var before = plan.Workouts.ToDictionary(w => w.Id, PlanAdjuster.Snapshot);
        var modified = new HashSet<PlannedWorkout>();
        foreach (var change in changes)
        {
            foreach (var w in PlanAdjuster.Apply(plan.Workouts, change, zones))
            {
                modified.Add(w);
            }
        }

        foreach (var w in modified)
        {
            fmt.FillTexts(athlete.Language, w);
            w.UpdatedAt = now;
            db.PlanChanges.Add(new PlanChange
            {
                PlanId = plan.Id,
                PlannedWorkoutId = w.Id,
                RecommendationId = rec.Id,
                BeforeJson = before[w.Id],
                AfterJson = PlanAdjuster.Snapshot(w),
                Reason = rec.Trigger,
                AppliedBy = appliedBy,
                AppliedAt = now,
            });
        }
    }

    private bool IsValid(TrainingPlan plan, IReadOnlyList<ProposedChange> changes, TrainingZones zones, out IReadOnlyList<Violation> violations)
    {
        if (changes.Count == 0)
        {
            violations = [];
            return true;
        }

        var after = PlanAdjuster.Preview(plan.Workouts, changes, zones);
        violations = new PlanValidator(safety.Value.ForRace(zones.RaceKm))
            .ValidateAdaptation(plan.Workouts, after, zones, allowIncrease: changes.Any(IsIncrease));
        return violations.Count == 0;
    }

    private static bool IsIncrease(ProposedChange c) =>
        c.Action is ChangeAction.IncreaseDistance or ChangeAction.Upgrade || c.ToKm > c.FromKm
        || c.ToType.IntensityRank() > c.FromType.IntensityRank();

    internal static TrainingZones Zones(TrainingPlan plan) =>
        CoachJson.Deserialize<PlanSnapshot>(plan.InputsJson)?.Zones ?? new TrainingZones(ZoneBasis.Rpe, null, null, null);
}
