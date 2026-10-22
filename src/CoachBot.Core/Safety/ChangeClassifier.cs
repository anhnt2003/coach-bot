using System.Globalization;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;

namespace CoachBot.Core.Safety;

/// <summary>
/// Decides whether a set of plan changes may be auto-applied (minor) or needs explicit athlete approval (major).
/// Anything that adds load, touches the taper, changes paces, or spans several workouts is major.
/// </summary>
public sealed class ChangeClassifier(SafetyOptions options)
{
    public ChangeMagnitude Classify(IReadOnlyList<ProposedChange> changes, DateOnly today, IReadOnlyDictionary<Guid, PlannedWorkout> workouts)
    {
        if (changes.Count != 1)
        {
            return ChangeMagnitude.Major;
        }

        var change = changes[0];
        if (change.Action is ChangeAction.IncreaseDistance or ChangeAction.Upgrade or ChangeAction.ChangePace)
        {
            return ChangeMagnitude.Major;
        }

        if (!workouts.TryGetValue(change.WorkoutId, out var workout) || workout.Phase == PlanPhase.Taper)
        {
            return ChangeMagnitude.Major;
        }

        if (change.Date < today || change.Date > today.AddDays(options.MinorChangeHorizonDays))
        {
            return ChangeMagnitude.Major;
        }

        if (change.ToKm > change.FromKm || change.ToType.IntensityRank() > change.FromType.IntensityRank())
        {
            return ChangeMagnitude.Major;
        }

        // Dropping a key session is major whatever the change is called (a "reduction" to rest drops it too).
        if (change.ToType == WorkoutType.Rest && change.FromType.IsHard())
        {
            return ChangeMagnitude.Major;
        }

        switch (change.Action)
        {
            case ChangeAction.SwapDays:
                return change.NewDate is DateOnly target && SameIsoWeek(change.Date, target)
                    ? ChangeMagnitude.Minor
                    : ChangeMagnitude.Major;

            case ChangeAction.ConvertToRest:
                // Dropping an easy run is minor; dropping a key session is not.
                return change.FromType.IsHard() ? ChangeMagnitude.Major : ChangeMagnitude.Minor;

            case ChangeAction.ReduceDistance or ChangeAction.Downgrade:
                var reduction = change.FromKm > 0 ? 1 - change.ToKm / change.FromKm : 0;
                if (change.FromType == WorkoutType.Long && reduction > options.LongRunMajorChangeShare + 1e-9)
                {
                    return ChangeMagnitude.Major;
                }

                return reduction > options.MinorMaxReduction + 1e-9 ? ChangeMagnitude.Major : ChangeMagnitude.Minor;

            default:
                return ChangeMagnitude.Major;
        }
    }

    private static bool SameIsoWeek(DateOnly a, DateOnly b) =>
        ISOWeek.GetYear(a.ToDateTime(TimeOnly.MinValue)) == ISOWeek.GetYear(b.ToDateTime(TimeOnly.MinValue))
        && ISOWeek.GetWeekOfYear(a.ToDateTime(TimeOnly.MinValue)) == ISOWeek.GetWeekOfYear(b.ToDateTime(TimeOnly.MinValue));
}
