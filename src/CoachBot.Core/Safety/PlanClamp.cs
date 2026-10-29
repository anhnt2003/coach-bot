using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Services;

namespace CoachBot.Core.Safety;

public sealed record ClampResult(List<PlannedWorkout> Workouts, IReadOnlyList<int> ClampedWeeks, IReadOnlyList<int> ReplacedWeeks);

/// <summary>
/// Brings an LLM-designed plan inside the enforced limits while keeping as much of the design as possible. A week over a
/// volume or long-run ceiling is cut down to it (easy runs first, then the rest of the week); a week the LLM got wrong in
/// a way a cut cannot fix (unreadable sessions, broken steps, too many run days) is replaced by the rule-based baseline's
/// week. Weeks are handled in date order because every ceiling depends on the weeks before it. Guideline violations are
/// left alone.
/// </summary>
public static class PlanClamp
{
    private const int MaxCutsPerWeek = 3;

    private static readonly HashSet<string> LongRunCeilings = ["long_run_over_cap", "long_run_over_duration_cap", "long_run_increase_too_large"];

    private static readonly HashSet<string> WeekCeilings =
        ["volume_increase_too_large", "cutback_not_reduced", "taper_not_reduced", "taper_increases", "race_week_not_tapered", "pre_race_week_not_tapered"];

    /// <param name="designErrors">Problems the mapper found; their sessions were dropped, so those weeks are replaced.</param>
    /// <returns>Null when the plan cannot be made valid (the caller keeps the rule-based plan).</returns>
    public static ClampResult? Apply(
        IReadOnlyList<PlannedWorkout> design, IReadOnlyList<Violation> designErrors, PlanDraft baseline, PlanValidator validator,
        TrainingZones zones, PlanConstraints constraints)
    {
        var enforcedErrors = designErrors.Where(e => !e.IsGuideline).ToList();
        if (enforcedErrors.Any(e => e.WeekIndex is null))
        {
            return null;
        }

        var workouts = design.Select(PlanAdjuster.Clone).ToList();
        var clamped = new SortedSet<int>();
        var replaced = new SortedSet<int>();
        var cuts = new Dictionary<int, int>();

        void Replace(int week)
        {
            workouts.RemoveAll(w => w.WeekIndex == week);
            workouts.AddRange(baseline.Workouts.Where(w => w.WeekIndex == week).Select(PlanAdjuster.Clone));
            replaced.Add(week);
            clamped.Remove(week);
        }

        foreach (var week in enforcedErrors.Select(e => e.WeekIndex!.Value).Distinct())
        {
            Replace(week);
        }

        var weeks = baseline.Workouts.Max(w => w.WeekIndex) + 1;
        for (var step = 0; step < weeks * (MaxCutsPerWeek + 2); step++)
        {
            var enforced = validator.ValidatePlan(workouts, baseline.Baseline, zones, constraints).Where(v => !v.IsGuideline).ToList();
            if (enforced.Count == 0)
            {
                return new([.. workouts.OrderBy(w => w.Date)], [.. clamped], [.. replaced]);
            }

            if (enforced.Any(v => v.WeekIndex is null))
            {
                return null;
            }

            var week = enforced.Min(v => v.WeekIndex!.Value);
            var ceilings = enforced.Where(v => v.WeekIndex == week && v.Limit is not null).ToList();
            var longLimit = ceilings.Where(v => LongRunCeilings.Contains(v.Code)).Select(v => v.Limit!.Value).DefaultIfEmpty(double.MaxValue).Min();
            var weekLimit = ceilings.Where(v => WeekCeilings.Contains(v.Code)).Select(v => v.Limit!.Value).DefaultIfEmpty(double.MaxValue).Min();
            var cuttable = ceilings.Count > 0 && ceilings.Count == enforced.Count(v => v.WeekIndex == week)
                && (longLimit < double.MaxValue || weekLimit < double.MaxValue);

            if (!replaced.Contains(week) && cuttable && cuts.GetValueOrDefault(week) < MaxCutsPerWeek)
            {
                cuts[week] = cuts.GetValueOrDefault(week) + 1;
                var runs = workouts.Where(w => w.WeekIndex == week && w.Type is not (WorkoutType.Race or WorkoutType.Rest)).ToList();
                if (longLimit < double.MaxValue)
                {
                    var target = Math.Max(PlanValidator.MinRunKm, PlanGenerator.Floor05(longLimit));
                    foreach (var w in runs.Where(w => w.TargetDistanceKm > target))
                    {
                        SetKm(workouts, w, target, zones);
                    }
                }

                if (weekLimit < double.MaxValue)
                {
                    // Easy mileage is what a coach trims first; it keeps the key sessions intact.
                    var excess = runs.Sum(w => w.TargetDistanceKm) - weekLimit;
                    var easy = runs.Where(w => w.Type is WorkoutType.Easy or WorkoutType.Recovery).ToList();
                    excess -= Trim(workouts, easy, excess, zones);
                    Trim(workouts, [.. runs.Except(easy)], excess, zones);
                }

                clamped.Add(week);
            }
            else if (!replaced.Contains(week))
            {
                Replace(week);
            }
            else
            {
                // A baseline week can only break because of an earlier LLM week it is measured against.
                var earlier = Enumerable.Range(0, week).Where(i => !replaced.Contains(i)).Select(i => (int?)i).LastOrDefault();
                if (earlier is not int e)
                {
                    return null;
                }

                Replace(e);
            }
        }

        return null;
    }

    /// <summary>Takes up to <paramref name="excess"/> km from the runs in proportion to their length above the minimum run.</summary>
    private static double Trim(List<PlannedWorkout> workouts, IReadOnlyList<PlannedWorkout> runs, double excess, TrainingZones zones)
    {
        var available = runs.Sum(w => w.TargetDistanceKm - PlanValidator.MinRunKm);
        if (excess <= 0 || available <= 0)
        {
            return 0;
        }

        var share = Math.Min(1, excess / available);
        var removed = 0.0;
        foreach (var w in runs)
        {
            var km = Math.Max(PlanValidator.MinRunKm, PlanGenerator.Floor05(w.TargetDistanceKm - (w.TargetDistanceKm - PlanValidator.MinRunKm) * share));
            removed += w.TargetDistanceKm - km;
            SetKm(workouts, w, km, zones);
        }

        return removed;
    }

    private static void SetKm(List<PlannedWorkout> workouts, PlannedWorkout w, double km, TrainingZones zones)
    {
        if (km >= w.TargetDistanceKm)
        {
            return;
        }

        var before = WorkoutStructure.Parse(w.StepsJson);
        PlanAdjuster.Apply(workouts, new ProposedChange(w.Id, w.Date, ChangeAction.ReduceDistance, w.Type, w.Type, w.TargetDistanceKm, km), zones);
        var after = WorkoutStructure.Parse(w.StepsJson);
        if (before is { Count: > 0 } && (after is null || !before.Select(b => b.Repeat).SequenceEqual(after.Select(b => b.Repeat))))
        {
            // The LLM's title ("6 × 1 km …") no longer matches the shortened main set; the default title describes the steps.
            w.Title = "";
        }
    }
}
