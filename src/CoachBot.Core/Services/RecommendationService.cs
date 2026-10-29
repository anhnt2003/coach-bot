using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
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
/// Owns every change to an approved plan: storing the LLM's changes, applying, approval, undo and expiry. Whether a
/// change waits for the athlete is the LLM's call. All changes are written to the <see cref="PlanChange"/> audit trail.
/// </summary>
public sealed class RecommendationService(
    ICoachDb db,
    MessageFormatter fmt,
    IOptions<CoachOptions> coach,
    TimeProvider time,
    ILogger<RecommendationService> logger)
{
    /// <summary>
    /// Stores the LLM's changes (already through the integrity gate). Those it marked as not needing approval are applied
    /// at once and can be undone; the others wait for the athlete. Up to two recommendations are created.
    /// </summary>
    public async Task<IReadOnlyList<Recommendation>> CreateAsync(
        Athlete athlete, TrainingPlan plan, IReadOnlyList<MappedChange> changes, string trigger, string summary, Guid? analysisId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var created = new List<Recommendation>();
        var auto = changes.Where(c => !c.NeedsApproval).Select(c => ToStored(athlete.Language, c)).ToList();
        var proposed = changes.Where(c => c.NeedsApproval).Select(c => ToStored(athlete.Language, c)).ToList();

        if (auto.Count > 0)
        {
            var rec = New(athlete, plan, trigger, summary, analysisId, auto, ChangeMagnitude.Minor, RecommendationStatus.AutoApplied, now);
            rec.DecidedAt = now;
            Apply(plan, rec, auto, "auto");
            created.Add(rec);
        }

        if (proposed.Count > 0)
        {
            created.Add(New(athlete, plan, trigger, summary, analysisId, proposed, ChangeMagnitude.Major, RecommendationStatus.Proposed, now));
        }

        if (created.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        foreach (var rec in created)
        {
            logger.LogInformation("Recommendation {Id} ({Trigger}, {Status}, {Count} changes)", rec.Id, rec.Trigger, rec.Status,
                rec.Status == RecommendationStatus.AutoApplied ? auto.Count : proposed.Count);
        }

        return created;
    }

    /// <summary>The change as shown to the athlete: the workout before, and after with its texts composed.</summary>
    private WorkoutChange ToStored(string lang, MappedChange c)
    {
        var preview = PlanAdjuster.Clone(c.Target);
        PlanAdjuster.Restore(preview, c.After);
        fmt.ComposeTexts(lang, preview, c.After.Title, c.After.Description);
        return new WorkoutChange(c.Target.Id, PlanAdjuster.State(c.Target), PlanAdjuster.State(preview),
            string.IsNullOrWhiteSpace(c.Reason) ? null : c.Reason.Trim(), c.NeedsApproval);
    }

    private Recommendation New(
        Athlete athlete, TrainingPlan plan, string trigger, string summary, Guid? analysisId, IReadOnlyList<WorkoutChange> changes,
        ChangeMagnitude magnitude, RecommendationStatus status, DateTimeOffset now)
    {
        var rec = new Recommendation
        {
            AthleteId = athlete.Id,
            PlanId = plan.Id,
            RunAnalysisId = analysisId,
            Trigger = trigger,
            Summary = summary,
            ChangesJson = CoachJson.Serialize(changes),
            Magnitude = magnitude,
            Status = status,
            CreatedAt = now,
            ExpiresAt = now.AddHours(coach.Value.ApprovalExpiryHours),
        };
        db.Recommendations.Add(rec);
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
        var changes = Changes(rec);

        // Apply exactly what the athlete was shown: if a workout changed since (completed, missed, another change), or
        // the change would now put two sessions on one day, the proposal no longer fits.
        if (changes.Count == 0 || plan.Status is not (PlanStatus.Active or PlanStatus.Paused) || !StillFits(plan, changes))
        {
            rec.Status = RecommendationStatus.Expired;
            rec.DecidedAt = now;
            await db.SaveChangesAsync(ct);
            return (DecisionOutcome.NoLongerValid, rec);
        }

        Apply(plan, rec, changes, "athlete");
        rec.Status = RecommendationStatus.Approved;
        rec.DecidedAt = now;
        await db.SaveChangesAsync(ct);
        return (DecisionOutcome.Done, rec);
    }

    private static bool StillFits(TrainingPlan plan, IReadOnlyList<WorkoutChange> changes)
    {
        foreach (var c in changes)
        {
            if (plan.Workouts.FirstOrDefault(w => w.Id == c.WorkoutId) is not { Status: WorkoutStatus.Planned } w || PlanAdjuster.State(w) != c.Before)
            {
                return false;
            }
        }

        var after = changes.ToDictionary(c => c.WorkoutId, c => c.After);
        return !plan.Workouts
            .Select(w => after.TryGetValue(w.Id, out var a) ? (w.Id, a.Date, a.Type) : (w.Id, w.Date, w.Type))
            .Where(s => s.Type != WorkoutType.Rest)
            .GroupBy(s => s.Date)
            .Any(g => g.Count() > 1 && g.Any(s => after.ContainsKey(s.Id)));
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

    /// <summary>Reverts an auto-applied change using the audit trail snapshots.</summary>
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

    /// <summary>Proposals that were not answered in time expire; the plan stays as it was.</summary>
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
        var lines = string.Join("\n", Changes(rec).Select(c => "• " + fmt.ChangeLine(lang, c)));
        if (rec.Status == RecommendationStatus.AutoApplied)
        {
            return new OutboundMessage($"{fmt.T(lang, "rec.auto_applied_header")}\n{lines}",
                [[new MessageButton(fmt.T(lang, "button.undo"), $"rec:undo:{rec.Id}")]]);
        }

        return new OutboundMessage($"{fmt.T(lang, "rec.proposed_header")}\n{lines}\n{fmt.T(lang, "rec.expires_hint", coach.Value.ApprovalExpiryHours)}",
            [[new MessageButton(fmt.T(lang, "button.approve"), $"rec:approve:{rec.Id}"), new MessageButton(fmt.T(lang, "button.reject"), $"rec:reject:{rec.Id}")]]);
    }

    /// <summary>The stored changes; rows from before changes carried full workout states have none and cannot be applied.</summary>
    private static List<WorkoutChange> Changes(Recommendation rec) =>
        (CoachJson.Deserialize<List<WorkoutChange>>(rec.ChangesJson) ?? []).Where(c => c.Before is not null && c.After is not null).ToList();

    private void Apply(TrainingPlan plan, Recommendation rec, IReadOnlyList<WorkoutChange> changes, string appliedBy)
    {
        var now = time.GetUtcNow();
        foreach (var change in changes)
        {
            var w = plan.Workouts.First(x => x.Id == change.WorkoutId);
            var before = PlanAdjuster.Snapshot(w);
            PlanAdjuster.Restore(w, change.After);
            w.UpdatedAt = now;
            db.PlanChanges.Add(new PlanChange
            {
                PlanId = plan.Id,
                PlannedWorkoutId = w.Id,
                RecommendationId = rec.Id,
                BeforeJson = before,
                AfterJson = PlanAdjuster.Snapshot(w),
                Reason = rec.Trigger,
                AppliedBy = appliedBy,
                AppliedAt = now,
            });
        }
    }
}
