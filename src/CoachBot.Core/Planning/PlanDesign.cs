using CoachBot.Core.Domain;
using CoachBot.Core.Safety;

namespace CoachBot.Core.Planning;

/// <summary>A plan as the LLM designs it: weekly structure only. Dates, paces and HR targets are filled in by code.</summary>
public sealed record PlanDesign(string? Rationale, IReadOnlyList<WeekDesign>? Weeks);

public sealed record WeekDesign(int Week, string? Phase, bool Cutback, string? Focus, IReadOnlyList<SessionDesign>? Sessions);

public sealed record SessionDesign(
    string Day, string Type, double Km, string? Notes = null, string? Title = null, IReadOnlyList<BlockDesign>? Steps = null);

/// <summary>A block as the LLM writes it, e.g. 6 × (1 km at five_k, 2 min recovery).</summary>
public sealed record BlockDesign(int Repeat, PartDesign? Work, PartDesign? Rest = null);

public sealed record PartDesign(string? Intensity, double? Km = null, double? Minutes = null);

/// <summary>
/// Converts between <see cref="PlanDesign"/> and planned workouts. Structural problems (unknown day/type/intensity, wrong
/// week numbers) are returned as violations so they can be sent back to the LLM together with the safety violations.
/// A session with steps gets its distance from the steps, and every pace comes from measured fitness.
/// </summary>
public static class PlanDesignMapper
{
    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public static (List<PlannedWorkout> Workouts, List<Violation> Errors) Map(
        PlanDesign design, DateOnly start, DateOnly raceDate, double raceKm, TrainingZones zones)
    {
        var errors = new List<Violation>();
        var workouts = new List<PlannedWorkout>();
        var expectedWeeks = (PlanGenerator.MondayOf(raceDate).DayNumber - start.DayNumber) / 7 + 1;
        var weeks = design.Weeks ?? [];

        // A missing or repeated week is reported against that week, so it can be repaired or replaced on its own. Weeks
        // outside the plan window are ignored.
        var numbers = weeks.Select(w => w.Week).ToList();
        foreach (var number in Enumerable.Range(1, expectedWeeks))
        {
            var count = numbers.Count(n => n == number);
            if (count != 1)
            {
                errors.Add(new("design_weeks_invalid", number - 1, null, $"week {number} given {count} times; expected weeks 1..{expectedWeeks} exactly once"));
            }
        }

        foreach (var week in weeks.Where(w => w.Week >= 1 && w.Week <= expectedWeeks))
        {
            var weekIndex = week.Week - 1;
            var weekStart = start.AddDays(7 * weekIndex);
            if (!TryPhase(week.Phase, out var phase))
            {
                errors.Add(new("design_unknown_phase", weekIndex, null, week.Phase ?? "null"));
            }

            foreach (var s in week.Sessions ?? [])
            {
                var day = (s.Day ?? "").Trim().ToLowerInvariant();
                var dayIndex = Array.IndexOf(Days, day.Length >= 3 ? day[..3] : day);
                if (dayIndex < 0)
                {
                    errors.Add(new("design_unknown_day", weekIndex, null, s.Day ?? "null"));
                    continue;
                }

                if (!TryType(s.Type, out var type))
                {
                    errors.Add(new("design_unknown_type", weekIndex, null, s.Type ?? "null"));
                    continue;
                }

                var date = weekStart.AddDays(dayIndex);
                if (type == WorkoutType.Rest || date < start)
                {
                    continue;
                }

                if (type == WorkoutType.Race && date != raceDate)
                {
                    errors.Add(new("race_on_wrong_day", weekIndex, date, $"race must be on {raceDate:yyyy-MM-dd}"));
                    continue;
                }

                List<WorkoutBlock>? blocks = null;
                if (type != WorkoutType.Race && s.Steps is { Count: > 0 })
                {
                    blocks = ToBlocks(s.Steps, zones, out var stepError);
                    if (stepError is not null)
                    {
                        errors.Add(new(stepError.Code, weekIndex, date, stepError.Detail));
                        continue;
                    }
                }

                var km = type == WorkoutType.Race ? raceKm
                    : blocks is not null ? Math.Round(WorkoutStructure.TotalKm(blocks, zones) * 2) / 2
                    : PlanGenerator.Floor05(s.Km);
                workouts.Add(Create(date, weekIndex, phase, week.Cutback, type, km, zones, s.Notes, s.Title, blocks));
            }
        }

        // The race itself is a fact, not a design choice: it is always on race day, replacing anything planned then.
        workouts.RemoveAll(w => w.Date == raceDate);
        var raceWeek = (raceDate.DayNumber - start.DayNumber) / 7;
        workouts.Add(Create(raceDate, raceWeek, PlanPhase.Taper, false, WorkoutType.Race, raceKm, zones, null, null, null));
        return ([.. workouts.OrderBy(w => w.Date)], errors);
    }

    /// <summary>Compact weekly view of a plan, used to show the LLM the rule-based baseline.</summary>
    public static IReadOnlyList<WeekDesign> ToDesign(IReadOnlyList<PlannedWorkout> workouts) =>
        [.. workouts.GroupBy(w => w.WeekIndex).OrderBy(g => g.Key).Select(g => new WeekDesign(
            g.Key + 1,
            g.First().Phase.ToString().ToLowerInvariant(),
            g.Any(w => w.IsCutbackWeek),
            null,
            [.. g.OrderBy(w => w.Date).Select(w => new SessionDesign(Days[PlanGenerator.MondayIndex(w.Date.DayOfWeek)], TypeName(w.Type), w.TargetDistanceKm))]))];

    public static string TypeName(WorkoutType type) => type == WorkoutType.RacePace ? "race_pace" : type.ToString().ToLowerInvariant();

    public static string IntensityName(Intensity intensity) => intensity switch
    {
        Intensity.HalfMarathon => "half_marathon",
        Intensity.TenK => "ten_k",
        Intensity.FiveK => "five_k",
        _ => intensity.ToString().ToLowerInvariant(),
    };

    private static bool TryType(string? value, out WorkoutType type)
    {
        type = WorkoutType.Easy;
        var normalized = Normalize(value);
        if (normalized is "marathonpace" or "halfmarathonpace" or "goalpace")
        {
            type = WorkoutType.RacePace;
            return true;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out type) && Enum.IsDefined(type);
    }

    private static bool TryIntensity(string? value, TrainingZones zones, out Intensity intensity)
    {
        intensity = Intensity.Easy;
        switch (Normalize(value))
        {
            case "racepace":
                intensity = zones.RaceIntensity;
                return true;
            case "vo2max":
                intensity = Intensity.FiveK;
                return true;
            case var n:
                return Enum.TryParse(n, ignoreCase: true, out intensity) && Enum.IsDefined(intensity);
        }
    }

    private static string? Normalize(string? value) =>
        value?.Trim().Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static List<WorkoutBlock>? ToBlocks(IReadOnlyList<BlockDesign> steps, TrainingZones zones, out StructureProblem? error)
    {
        var blocks = new List<WorkoutBlock>();
        foreach (var step in steps)
        {
            if (step.Work is null)
            {
                error = new("steps_invalid", "every step needs a work part");
                return null;
            }

            if (!TryIntensity(step.Work.Intensity, zones, out var work))
            {
                error = new("design_unknown_intensity", step.Work.Intensity ?? "null");
                return null;
            }

            StepPart? rest = null;
            if (step.Rest is not null)
            {
                if (!TryIntensity(step.Rest.Intensity, zones, out var restIntensity))
                {
                    error = new("design_unknown_intensity", step.Rest.Intensity ?? "null");
                    return null;
                }

                rest = Part(restIntensity, step.Rest);
            }

            blocks.Add(new WorkoutBlock(step.Repeat, Part(work, step.Work), rest));
        }

        error = null;
        return WorkoutStructure.Resolve(blocks, zones);
    }

    // A part is a distance or a duration; when the LLM gives both, the distance wins.
    private static StepPart Part(Intensity intensity, PartDesign p) =>
        p.Km is > 0 ? new StepPart(intensity, Km: p.Km) : new StepPart(intensity, Minutes: p.Minutes);

    private static bool TryPhase(string? value, out PlanPhase phase)
    {
        phase = PlanPhase.Build;
        return Enum.TryParse(value, ignoreCase: true, out phase) && Enum.IsDefined(phase);
    }

    private static PlannedWorkout Create(
        DateOnly date, int weekIndex, PlanPhase phase, bool cutback, WorkoutType type, double km, TrainingZones zones,
        string? notes, string? title, List<WorkoutBlock>? blocks)
    {
        // Quality sessions show the pace of their main set; steady sessions the pace of the bulk of the run.
        var main = blocks is not null && type.IsQuality() ? WorkoutStructure.MainIntensity(blocks) : null;
        var pace = main is Intensity m ? zones.PaceFor(m) : zones.PaceFor(type);
        var hr = main is Intensity mh ? zones.HrFor(mh) : zones.HrFor(type);
        var minutes = blocks is null
            ? pace is null ? (int?)null : (int)Math.Round(km * (pace.Fast + pace.Slow) / 2.0 / 60.0)
            : zones.HasPace ? (int)Math.Round(blocks.Sum(b => b.Repeat * (Minutes(b.Work, zones) + (b.Rest is null ? 0 : Minutes(b.Rest, zones))))) : null;
        return new PlannedWorkout
        {
            Date = date,
            WeekIndex = weekIndex,
            Phase = phase,
            IsCutbackWeek = cutback,
            Type = type,
            TargetDistanceKm = km,
            PaceMinSecPerKm = pace?.Fast,
            PaceMaxSecPerKm = pace?.Slow,
            HrMin = hr?.Min,
            HrMax = hr?.Max,
            TargetDurationMinutes = minutes,
            // Carried to PlanService, which combines the LLM's title and note with the templated targets.
            Title = string.IsNullOrWhiteSpace(title) ? "" : title.Trim(),
            Description = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            StepsJson = WorkoutStructure.Serialize(blocks),
            Status = WorkoutStatus.Planned,
            CorosSyncStatus = SyncStatus.NotSynced,
        };
    }

    private static double Minutes(StepPart p, TrainingZones zones) =>
        p.Minutes ?? WorkoutStructure.PartKm(p, zones) * zones.ConversionPaceFor(p.Intensity) / 60.0;
}
