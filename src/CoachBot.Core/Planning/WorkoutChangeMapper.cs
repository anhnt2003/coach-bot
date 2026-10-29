using System.Globalization;
using CoachBot.Core.Domain;

namespace CoachBot.Core.Planning;

/// <summary>A change the LLM proposed, mapped onto the workout it changes. <see cref="After"/> carries the LLM's raw title and note.</summary>
public sealed record MappedChange(PlannedWorkout Target, WorkoutState After, string? Reason, bool NeedsApproval);

/// <summary>
/// Turns the LLM's proposed workout changes into new workout states, and is the integrity gate for them: a change must
/// name a session the LLM was offered, land between today and race day, use known names and readable steps, and leave
/// at most one session per day. How much the athlete runs is the coach's choice and is not checked.
/// </summary>
public static class WorkoutChangeMapper
{
    /// <param name="offered">Sessions the LLM was allowed to change: planned, not the race, today or later.</param>
    public static (List<MappedChange> Changes, List<string> Errors) Map(
        IReadOnlyList<ChangeDesign>? designs, TrainingPlan plan, IReadOnlySet<Guid> offered, PaceTable paces, DateOnly today, DateOnly raceDate)
    {
        var errors = new List<string>();
        var changes = new List<MappedChange>();
        var seen = new HashSet<Guid>();
        var last = plan.EndDate < raceDate ? plan.EndDate : raceDate;
        foreach (var d in designs ?? [])
        {
            if (!Guid.TryParse(d.WorkoutId, out var id) || !offered.Contains(id))
            {
                errors.Add($"change_unknown_workout: {d.WorkoutId ?? "null"} is not one of the planned sessions you may change");
                continue;
            }

            if (!seen.Add(id))
            {
                errors.Add($"change_duplicate_workout ({id}): one change per session");
                continue;
            }

            if (!DateOnly.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date < today || date > last)
            {
                errors.Add($"change_date_out_of_range ({id}): {d.Date ?? "null"}; use yyyy-MM-dd from {today:yyyy-MM-dd} to {last:yyyy-MM-dd}");
                continue;
            }

            if (!PlanDesignMapper.TryType(d.Type, out var type) || type == WorkoutType.Race)
            {
                errors.Add($"change_unknown_type ({id}): {d.Type ?? "null"}");
                continue;
            }

            Intensity? intensity = null;
            if (d.Intensity is not null && type != WorkoutType.Rest)
            {
                if (!PlanDesignMapper.TryIntensity(d.Intensity, paces.RaceKm, out var i))
                {
                    errors.Add($"change_unknown_intensity ({id}): {d.Intensity}");
                    continue;
                }

                intensity = i;
            }

            List<WorkoutBlock>? blocks = null;
            double km = 0;
            if (type != WorkoutType.Rest)
            {
                if (d.Steps is { Count: > 0 })
                {
                    blocks = PlanDesignMapper.ToBlocks(d.Steps, paces, out var stepError);
                    if (stepError is not null)
                    {
                        errors.Add($"{stepError.Code} ({id}): {stepError.Detail}");
                        continue;
                    }

                    km = Math.Round(WorkoutStructure.TotalKm(blocks!, paces) * 2) / 2;
                }
                else
                {
                    km = PlanCalendar.Floor05(d.Km);
                }

                if (km <= 0)
                {
                    errors.Add($"change_km_invalid ({id}): {d.Km.ToString(CultureInfo.InvariantCulture)} km; use type rest to drop a session");
                    continue;
                }
            }

            var t = PlanDesignMapper.Targets(type, km, blocks, intensity, paces);
            var after = new WorkoutState(date, type, km, t.Minutes, t.PaceMin, t.PaceMax, t.HrMin, t.HrMax,
                string.IsNullOrWhiteSpace(d.Title) ? "" : d.Title.Trim(), string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim(),
                WorkoutStructure.Serialize(blocks));
            changes.Add(new MappedChange(plan.Workouts.First(w => w.Id == id), after, d.Reason, d.NeedsApproval));
        }

        // After the changes, a day holds at most one session. Collisions that existed before are not the LLM's doing.
        var changed = changes.ToDictionary(c => c.Target.Id);
        var sessions = plan.Workouts
            .Select(w => changed.TryGetValue(w.Id, out var c) ? (w.Id, c.After.Date, c.After.Type) : (w.Id, w.Date, w.Type))
            .Where(s => s.Type != WorkoutType.Rest);
        foreach (var day in sessions.GroupBy(s => s.Date).Where(g => g.Count() > 1 && g.Any(s => changed.ContainsKey(s.Id))))
        {
            errors.Add($"change_duplicate_date ({day.Key:yyyy-MM-dd}): two sessions on one day; move the other session too or make one of them rest");
        }

        return (changes, errors);
    }
}
