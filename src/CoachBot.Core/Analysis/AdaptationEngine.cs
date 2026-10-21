using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Safety;

namespace CoachBot.Core.Analysis;

public sealed record AdaptationContext(
    DateOnly Today,
    ReadinessAssessment Readiness,
    IReadOnlyList<ReadinessLevel> PreviousLevels, // previous days, most recent first
    IReadOnlyList<PlannedWorkout> Upcoming, // planned workouts of the active plan from today onward
    int? CurrentThresholdPaceSecPerKm,
    int? PlanThresholdPaceSecPerKm);

public sealed record AdaptationProposal(IReadOnlyList<ProposedChange> Changes, string ReasonKey)
{
    public static AdaptationProposal None { get; } = new([], "adapt.none");
    public bool IsEmpty => Changes.Count == 0;
}

/// <summary>
/// Rule-based plan adaptation. Fatigue only ever reduces load. Load increases (faster paces after a fitness gain)
/// are always proposed as major changes, so they wait for the athlete's approval.
/// </summary>
public static class AdaptationEngine
{
    public static AdaptationProposal Propose(AdaptationContext ctx)
    {
        var level = ctx.Readiness.Level;
        var prev = ctx.PreviousLevels;
        var upcoming = ctx.Upcoming
            .Where(w => w.Status == WorkoutStatus.Planned && w.Date >= ctx.Today && w.Type != WorkoutType.Rest && w.Type != WorkoutType.Race)
            .OrderBy(w => w.Date)
            .ToList();

        if (upcoming.Count == 0)
        {
            return AdaptationProposal.None;
        }

        var redStreak = level == ReadinessLevel.Red && prev.Count > 0 && prev[0] == ReadinessLevel.Red;
        var poorStreak = level != ReadinessLevel.Green && prev.Take(2).Count(l => l != ReadinessLevel.Green) == 2;

        if (redStreak || (level == ReadinessLevel.Red && poorStreak))
        {
            return Deload(ctx.Today, upcoming);
        }

        if (level == ReadinessLevel.Red || poorStreak)
        {
            return EaseNextSession(ctx.Today, upcoming);
        }

        if (level == ReadinessLevel.Amber)
        {
            return SoftenNextHardSession(ctx.Today, upcoming);
        }

        return PaceUpdate(ctx, upcoming);
    }

    /// <summary>Two bad days in a row: reduce the rest of this week by 30% and drop intensity.</summary>
    private static AdaptationProposal Deload(DateOnly today, List<PlannedWorkout> upcoming)
    {
        var weekIndex = upcoming[0].WeekIndex;
        var changes = upcoming
            .Where(w => w.WeekIndex == weekIndex && w.Date <= today.AddDays(6))
            .Select(w =>
            {
                var toKm = PlanGenerator.Floor05(w.TargetDistanceKm * 0.7);
                var toType = w.Type.IsQuality() ? WorkoutType.Easy : w.Type;
                return toKm < 3
                    ? new ProposedChange(w.Id, w.Date, ChangeAction.ConvertToRest, w.Type, WorkoutType.Rest, w.TargetDistanceKm, 0)
                    : new ProposedChange(w.Id, w.Date, toType == w.Type ? ChangeAction.ReduceDistance : ChangeAction.Downgrade,
                        w.Type, toType, w.TargetDistanceKm, toKm);
            })
            .ToList();

        return new AdaptationProposal(changes, "adapt.deload");
    }

    /// <summary>Red day: the next session (today or tomorrow) becomes recovery, or rest if it was already easy.</summary>
    private static AdaptationProposal EaseNextSession(DateOnly today, List<PlannedWorkout> upcoming)
    {
        var next = upcoming.FirstOrDefault(w => w.Date <= today.AddDays(1));
        if (next is null)
        {
            return AdaptationProposal.None;
        }

        var change = next.Type.IsHard()
            ? new ProposedChange(next.Id, next.Date, ChangeAction.Downgrade, next.Type, WorkoutType.Recovery,
                next.TargetDistanceKm, PlanGenerator.Floor05(next.TargetDistanceKm * 0.7))
            : new ProposedChange(next.Id, next.Date, ChangeAction.ConvertToRest, next.Type, WorkoutType.Rest, next.TargetDistanceKm, 0);

        return new AdaptationProposal([change], "adapt.red_day");
    }

    /// <summary>Amber day: soften the next hard session within two days; easy days are left alone.</summary>
    private static AdaptationProposal SoftenNextHardSession(DateOnly today, List<PlannedWorkout> upcoming)
    {
        var next = upcoming.FirstOrDefault(w => w.Date <= today.AddDays(2) && w.Type.IsHard());
        if (next is null)
        {
            return AdaptationProposal.None;
        }

        var toKm = PlanGenerator.Floor05(next.TargetDistanceKm * 0.8);
        var change = next.Type.IsQuality()
            ? new ProposedChange(next.Id, next.Date, ChangeAction.Downgrade, next.Type, WorkoutType.Easy, next.TargetDistanceKm, toKm)
            : new ProposedChange(next.Id, next.Date, ChangeAction.ReduceDistance, next.Type, next.Type, next.TargetDistanceKm, toKm);

        return new AdaptationProposal([change], "adapt.amber_day");
    }

    /// <summary>Green day with a ≥3% threshold change: propose new pace targets for the next two weeks of quality work.</summary>
    private static AdaptationProposal PaceUpdate(AdaptationContext ctx, List<PlannedWorkout> upcoming)
    {
        if (ctx.CurrentThresholdPaceSecPerKm is not int current || ctx.PlanThresholdPaceSecPerKm is not int planned || planned <= 0)
        {
            return AdaptationProposal.None;
        }

        var ratio = (double)current / planned;
        if (ratio is > 0.97 and < 1.03)
        {
            return AdaptationProposal.None;
        }

        var changes = upcoming
            .Where(w => w.Type.IsQuality() && w.Date <= ctx.Today.AddDays(14) && w.PaceMinSecPerKm is not null && w.PaceMaxSecPerKm is not null)
            .Select(w => new ProposedChange(w.Id, w.Date, ChangeAction.ChangePace, w.Type, w.Type, w.TargetDistanceKm, w.TargetDistanceKm,
                NewPaceMinSecPerKm: (int)Math.Round(w.PaceMinSecPerKm!.Value * ratio),
                NewPaceMaxSecPerKm: (int)Math.Round(w.PaceMaxSecPerKm!.Value * ratio)))
            .ToList();

        return changes.Count == 0
            ? AdaptationProposal.None
            : new AdaptationProposal(changes, ratio < 1 ? "adapt.fitness_improved" : "adapt.fitness_declined");
    }

    /// <summary>Largest single-session distance increase an LLM proposal may contain (km).</summary>
    public const double MaxLlmSessionIncreaseKm = 5;

    /// <summary>
    /// Filters and merges the LLM's proposals with the rule-based changes. Rules win on conflicts.
    /// Returns two groups that are validated and approved separately:
    /// <list type="bullet">
    /// <item><b>Easing</b>: rule changes plus LLM reductions, downgrades, rest days and day swaps.</item>
    /// <item><b>Progression</b>: LLM distance increases and session upgrades, only when readiness is green with at least
    /// medium confidence; they always need the athlete's approval (major).</item>
    /// </list>
    /// The "before" state is always rebuilt from the database, never taken from the LLM. LLM pace changes are ignored:
    /// paces come from measured fitness.
    /// </summary>
    public static (IReadOnlyList<ProposedChange> Easing, IReadOnlyList<ProposedChange> Progression) MergeLlmProposals(
        IReadOnlyList<ProposedChange> ruleChanges, IReadOnlyList<ProposedChange> llmChanges,
        IReadOnlyDictionary<Guid, PlannedWorkout> workouts, DateOnly today, ReadinessAssessment? readiness)
    {
        var horizon = today.AddDays(14);
        var mayProgress = readiness is { Level: ReadinessLevel.Green, Confidence: not Confidence.Low };
        var taken = ruleChanges.Select(c => c.WorkoutId).ToHashSet();
        var easing = new List<ProposedChange>(ruleChanges);
        var progression = new List<ProposedChange>();

        foreach (var c in llmChanges)
        {
            if (taken.Contains(c.WorkoutId) || !workouts.TryGetValue(c.WorkoutId, out var w)
                || w.Status != WorkoutStatus.Planned || w.Type is WorkoutType.Race or WorkoutType.Rest
                || w.Date < today || w.Date > horizon)
            {
                continue;
            }

            var rebuilt = c with { Date = w.Date, FromType = w.Type, FromKm = w.TargetDistanceKm, NewPaceMinSecPerKm = null, NewPaceMaxSecPerKm = null };
            switch (c.Action)
            {
                case ChangeAction.ReduceDistance or ChangeAction.Downgrade when c.ToKm > 0 && c.ToKm <= w.TargetDistanceKm
                                                                             && c.ToType != WorkoutType.Race
                                                                             && c.ToType.IntensityRank() <= w.Type.IntensityRank():
                    easing.Add(rebuilt with { NewDate = null });
                    break;
                case ChangeAction.ConvertToRest:
                    easing.Add(rebuilt with { ToType = WorkoutType.Rest, ToKm = 0, NewDate = null });
                    break;
                case ChangeAction.SwapDays when c.NewDate is DateOnly target && target != w.Date && target >= today && target <= horizon
                                                && !workouts.Values.Any(o => o.Date == target && o.Type == WorkoutType.Race):
                    easing.Add(rebuilt with { ToType = w.Type, ToKm = w.TargetDistanceKm });
                    break;
                case ChangeAction.IncreaseDistance when mayProgress && c.ToKm > w.TargetDistanceKm
                                                       && c.ToKm <= w.TargetDistanceKm + MaxLlmSessionIncreaseKm:
                    progression.Add(rebuilt with { ToType = w.Type, NewDate = null });
                    break;
                case ChangeAction.Upgrade when mayProgress && c.ToType.IntensityRank() > w.Type.IntensityRank() && c.ToType != WorkoutType.Race
                                              && c.ToKm >= 3 && c.ToKm <= w.TargetDistanceKm + MaxLlmSessionIncreaseKm:
                    progression.Add(rebuilt with { NewDate = null });
                    break;
                default:
                    continue;
            }

            taken.Add(c.WorkoutId);
        }

        return (easing, progression);
    }
}
