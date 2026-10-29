using System.Globalization;
using CoachBot.Core.Domain;

namespace CoachBot.Core.Planning;

/// <summary>A plan as the LLM designs it: its pace table and weekly structure. Dates are filled in by code.</summary>
public sealed record PlanDesign(string? Rationale, IReadOnlyList<WeekDesign>? Weeks, IReadOnlyList<PaceDesign>? Paces);

public sealed record WeekDesign(int Week, string? Phase, bool Cutback, string? Focus, IReadOnlyList<SessionDesign>? Sessions);

/// <param name="Intensity">Effort of a plain run; null means the usual intensity for its type.</param>
public sealed record SessionDesign(
    string? Day, string? Type, double Km, string? Notes = null, string? Title = null, IReadOnlyList<BlockDesign>? Steps = null,
    string? Intensity = null);

/// <summary>A block as the LLM writes it, e.g. 6 × (1 km at five_k, 2 min recovery).</summary>
public sealed record BlockDesign(int Repeat, PartDesign? Work, PartDesign? Rest = null);

public sealed record PartDesign(string? Intensity, double? Km = null, double? Minutes = null);

/// <summary>One row of the LLM's pace table, in seconds per km.</summary>
public sealed record PaceDesign(string? Intensity, int PaceFastSecPerKm, int PaceSlowSecPerKm, int? HrMin = null, int? HrMax = null);

/// <summary>The complete new state of one planned workout, as the LLM proposes it after a run or in the morning check.</summary>
/// <param name="Date">yyyy-MM-dd</param>
/// <param name="NeedsApproval">False: applied at once, with Undo. True: waits for the athlete's approval.</param>
public sealed record ChangeDesign(
    string? WorkoutId, string? Date, string? Type, double Km, string? Intensity = null, string? Title = null, string? Notes = null,
    IReadOnlyList<BlockDesign>? Steps = null, string? Reason = null, bool NeedsApproval = true);

/// <summary>A mapped plan design. Any error means the design is unusable and nothing is stored.</summary>
public sealed record MappedPlan(List<PlannedWorkout> Workouts, PaceTable Paces, List<string> Errors);

/// <summary>
/// Turns the LLM's plan design into planned workouts, and is the integrity gate for it: every problem that would make the
/// design impossible to store or schedule (unknown names, missing weeks, two sessions on one day, a race on the wrong
/// day, unreadable steps or paces) is returned as an error. What the plan contains is the coach's choice and is not
/// checked. Paces and heart rates come from the design's own pace table.
/// </summary>
public static class PlanDesignMapper
{
    private const int MinPaceSecPerKm = 120;
    private const int MaxPaceSecPerKm = 900;
    private const int MinHr = 30;
    private const int MaxHr = 230;

    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public static MappedPlan Map(PlanDesign design, DateOnly start, DateOnly raceDate, double raceKm)
    {
        var errors = new List<string>();
        var paces = ParsePaces(design.Paces, raceKm, errors);
        var workouts = new List<PlannedWorkout>();
        var expectedWeeks = PlanCalendar.WeeksUntil(start, raceDate);
        var weeks = design.Weeks ?? [];

        var numbers = weeks.Select(w => w.Week).ToList();
        foreach (var number in Enumerable.Range(1, expectedWeeks))
        {
            var count = numbers.Count(n => n == number);
            if (count != 1)
            {
                errors.Add($"design_weeks_invalid (week {number}): given {count} times; expected weeks 1..{expectedWeeks} exactly once");
            }
        }

        foreach (var week in weeks.Where(w => w.Week >= 1 && w.Week <= expectedWeeks))
        {
            var weekIndex = week.Week - 1;
            var weekStart = start.AddDays(7 * weekIndex);
            if (!TryPhase(week.Phase, out var phase))
            {
                errors.Add($"design_unknown_phase (week {week.Week}): {week.Phase ?? "null"}");
            }

            foreach (var s in week.Sessions ?? [])
            {
                var day = (s.Day ?? "").Trim().ToLowerInvariant();
                var dayIndex = Array.IndexOf(Days, day.Length >= 3 ? day[..3] : day);
                if (dayIndex < 0)
                {
                    errors.Add($"design_unknown_day (week {week.Week}): {s.Day ?? "null"}");
                    continue;
                }

                if (!TryType(s.Type, out var type))
                {
                    errors.Add($"design_unknown_type (week {week.Week}): {s.Type ?? "null"}");
                    continue;
                }

                var date = weekStart.AddDays(dayIndex);
                var where = $"week {week.Week}, {date:yyyy-MM-dd}";
                if (type == WorkoutType.Rest)
                {
                    continue;
                }

                if (date > raceDate)
                {
                    errors.Add($"session_after_race ({where}): the race is on {raceDate:yyyy-MM-dd}");
                    continue;
                }

                if (type == WorkoutType.Race && date != raceDate)
                {
                    errors.Add($"race_on_wrong_day ({where}): the race is on {raceDate:yyyy-MM-dd}");
                    continue;
                }

                Intensity? intensity = null;
                if (s.Intensity is not null)
                {
                    if (!TryIntensity(s.Intensity, raceKm, out var i))
                    {
                        errors.Add($"design_unknown_intensity ({where}): {s.Intensity}");
                        continue;
                    }

                    intensity = i;
                }

                List<WorkoutBlock>? blocks = null;
                if (type != WorkoutType.Race && s.Steps is { Count: > 0 })
                {
                    blocks = ToBlocks(s.Steps, paces, out var stepError);
                    if (stepError is not null)
                    {
                        errors.Add($"{stepError.Code} ({where}): {stepError.Detail}");
                        continue;
                    }
                }

                var km = type == WorkoutType.Race ? raceKm
                    : blocks is not null ? Math.Round(WorkoutStructure.TotalKm(blocks, paces) * 2) / 2
                    : PlanCalendar.Floor05(s.Km);
                if (km <= 0)
                {
                    errors.Add($"session_km_invalid ({where}): {s.Km.ToString(CultureInfo.InvariantCulture)} km; give the distance or steps that add up to it");
                    continue;
                }

                workouts.Add(Create(date, weekIndex, phase, week.Cutback, type, km, paces, intensity, s.Notes, s.Title, blocks));
            }
        }

        // The race itself is a fact, not a design choice: it is always on race day, replacing anything planned then.
        workouts.RemoveAll(w => w.Date == raceDate);
        foreach (var date in workouts.GroupBy(w => w.Date).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            errors.Add($"two_sessions_same_day ({date:yyyy-MM-dd}): at most one session per day");
        }

        var raceWeek = (raceDate.DayNumber - start.DayNumber) / 7;
        workouts.Add(Create(raceDate, raceWeek, PlanPhase.Taper, false, WorkoutType.Race, raceKm, paces, null, null, null, null));
        return new MappedPlan([.. workouts.OrderBy(w => w.Date)], paces, errors);
    }

    /// <summary>Reads the LLM's pace table. Paces are seconds per km; an intensity may appear once.</summary>
    public static PaceTable ParsePaces(IReadOnlyList<PaceDesign>? designs, double raceKm, List<string> errors)
    {
        var entries = new List<PaceEntry>();
        foreach (var p in designs ?? [])
        {
            if (!TryIntensity(p.Intensity, raceKm, out var intensity))
            {
                errors.Add($"pace_unknown_intensity: {p.Intensity ?? "null"}");
                continue;
            }

            var name = IntensityName(intensity);
            if (p.PaceFastSecPerKm is < MinPaceSecPerKm or > MaxPaceSecPerKm || p.PaceSlowSecPerKm is < MinPaceSecPerKm or > MaxPaceSecPerKm
                || p.PaceFastSecPerKm > p.PaceSlowSecPerKm)
            {
                errors.Add($"pace_invalid ({name}): fast {p.PaceFastSecPerKm}, slow {p.PaceSlowSecPerKm}; seconds per km between {MinPaceSecPerKm} and {MaxPaceSecPerKm}, fast <= slow");
                continue;
            }

            if ((p.HrMin is null) != (p.HrMax is null) || p.HrMin is < MinHr or > MaxHr || p.HrMax is < MinHr or > MaxHr || p.HrMin > p.HrMax)
            {
                errors.Add($"hr_invalid ({name}): {p.HrMin}–{p.HrMax}; give both bounds between {MinHr} and {MaxHr}, min <= max, or neither");
                continue;
            }

            if (entries.Any(e => e.Intensity == intensity))
            {
                errors.Add($"pace_duplicate ({name}): one entry per intensity (race_pace is {name} for this goal)");
                continue;
            }

            entries.Add(new PaceEntry(intensity, p.PaceFastSecPerKm, p.PaceSlowSecPerKm, p.HrMin, p.HrMax));
        }

        return new PaceTable(entries, raceKm);
    }

    public static string TypeName(WorkoutType type) => type == WorkoutType.RacePace ? "race_pace" : type.ToString().ToLowerInvariant();

    public static string IntensityName(Intensity intensity) => intensity switch
    {
        Intensity.HalfMarathon => "half_marathon",
        Intensity.TenK => "ten_k",
        Intensity.FiveK => "five_k",
        _ => intensity.ToString().ToLowerInvariant(),
    };

    internal static bool TryType(string? value, out WorkoutType type)
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

    /// <summary>"race_pace" is the goal distance's intensity; "vo2max" is 5K pace.</summary>
    internal static bool TryIntensity(string? value, double raceKm, out Intensity intensity)
    {
        intensity = Intensity.Easy;
        switch (Normalize(value))
        {
            case "racepace":
                intensity = RaceProfiles.RaceIntensity(raceKm);
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

    /// <summary>Reads the steps and fills in their paces; an unreadable step is an error.</summary>
    internal static List<WorkoutBlock>? ToBlocks(IReadOnlyList<BlockDesign> steps, PaceTable paces, out StructureProblem? error)
    {
        var blocks = new List<WorkoutBlock>();
        foreach (var step in steps)
        {
            if (step.Work is null)
            {
                error = new("steps_invalid", "every step needs a work part");
                return null;
            }

            if (!TryIntensity(step.Work.Intensity, paces.RaceKm, out var work))
            {
                error = new("design_unknown_intensity", step.Work.Intensity ?? "null");
                return null;
            }

            StepPart? rest = null;
            if (step.Rest is not null)
            {
                if (!TryIntensity(step.Rest.Intensity, paces.RaceKm, out var restIntensity))
                {
                    error = new("design_unknown_intensity", step.Rest.Intensity ?? "null");
                    return null;
                }

                rest = Part(restIntensity, step.Rest);
            }

            blocks.Add(new WorkoutBlock(step.Repeat, Part(work, step.Work), rest));
        }

        if (WorkoutStructure.Problems(blocks) is { Count: > 0 } problems)
        {
            error = problems[0];
            return null;
        }

        error = null;
        return WorkoutStructure.Resolve(blocks, paces);
    }

    // A part is a distance or a duration; when the LLM gives both, the distance wins.
    private static StepPart Part(Intensity intensity, PartDesign p) =>
        p.Km is > 0 ? new StepPart(intensity, Km: p.Km) : new StepPart(intensity, Minutes: p.Minutes);

    private static bool TryPhase(string? value, out PlanPhase phase)
    {
        phase = PlanPhase.Build;
        return Enum.TryParse(value, ignoreCase: true, out phase) && Enum.IsDefined(phase);
    }

    /// <summary>
    /// Pace, heart rate and duration targets looked up in the pace table: a quality session's main set, else the
    /// session's own intensity, else the usual intensity for its type. Duration is computed only when every part has a pace.
    /// </summary>
    internal static (int? PaceMin, int? PaceMax, int? HrMin, int? HrMax, int? Minutes) Targets(
        WorkoutType type, double km, IReadOnlyList<WorkoutBlock>? blocks, Intensity? sessionIntensity, PaceTable paces)
    {
        if (type == WorkoutType.Rest)
        {
            return (null, null, null, null, null);
        }

        var headline = (blocks is not null && type.IsQuality() ? WorkoutStructure.MainIntensity(blocks) : null)
                       ?? sessionIntensity ?? paces.DefaultIntensity(type);
        var pace = headline is Intensity h ? paces.PaceFor(h) : null;
        var hr = headline is Intensity hh ? paces.HrFor(hh) : null;
        int? minutes;
        if (blocks is null)
        {
            minutes = pace is null ? null : (int)Math.Round(km * (pace.Fast + pace.Slow) / 2.0 / 60.0);
        }
        else
        {
            bool Known(StepPart p) => p.Minutes is not null || paces.PaceFor(p.Intensity) is not null;
            minutes = blocks.All(b => Known(b.Work) && (b.Rest is null || Known(b.Rest)))
                ? (int)Math.Round(blocks.Sum(b => b.Repeat * (Minutes(b.Work, paces) + (b.Rest is null ? 0 : Minutes(b.Rest, paces)))))
                : null;
        }

        return (pace?.Fast, pace?.Slow, hr?.Min, hr?.Max, minutes);
    }

    private static PlannedWorkout Create(
        DateOnly date, int weekIndex, PlanPhase phase, bool cutback, WorkoutType type, double km, PaceTable paces, Intensity? intensity,
        string? notes, string? title, List<WorkoutBlock>? blocks)
    {
        var t = Targets(type, km, blocks, intensity, paces);
        return new PlannedWorkout
        {
            Date = date,
            WeekIndex = weekIndex,
            Phase = phase,
            IsCutbackWeek = cutback,
            Type = type,
            TargetDistanceKm = km,
            PaceMinSecPerKm = t.PaceMin,
            PaceMaxSecPerKm = t.PaceMax,
            HrMin = t.HrMin,
            HrMax = t.HrMax,
            TargetDurationMinutes = t.Minutes,
            // Carried to PlanService, which combines the LLM's title and note with the targets.
            Title = string.IsNullOrWhiteSpace(title) ? "" : title.Trim(),
            Description = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            StepsJson = WorkoutStructure.Serialize(blocks),
            Status = WorkoutStatus.Planned,
            CorosSyncStatus = SyncStatus.NotSynced,
        };
    }

    private static double Minutes(StepPart p, PaceTable paces) =>
        p.Minutes ?? WorkoutStructure.PartKm(p, paces) * paces.ConversionPaceSecPerKm(p.Intensity) / 60.0;
}
