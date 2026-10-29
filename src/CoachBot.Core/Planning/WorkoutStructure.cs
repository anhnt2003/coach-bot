using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Services;

namespace CoachBot.Core.Planning;

/// <summary>
/// One part of a block: a distance or a duration at one intensity. The pace range is filled in from measured fitness
/// when the workout is created (null for hills, strides, or without pace data).
/// </summary>
public sealed record StepPart(Intensity Intensity, double? Km = null, double? Minutes = null, int? PaceFast = null, int? PaceSlow = null);

/// <summary>A block of a structured workout, run <see cref="Repeat"/> times: the work part, then an optional recovery part.</summary>
public sealed record WorkoutBlock(int Repeat, StepPart Work, StepPart? Rest = null);

/// <summary>Intensity groups with a per-session limit (<see cref="Abstractions.SafetyOptions"/>).</summary>
public enum IntensityBand
{
    None,
    RacePace,
    Threshold,
    Vo2,
    Repetition,
}

public sealed record StructureProblem(string Code, string Detail);

/// <summary>
/// Measures and checks structured workouts. Safety rules look at what the steps actually contain, so intensity hidden in
/// a session labelled "easy" is caught, and the time spent at each intensity counts towards the weekly limits.
/// </summary>
public static class WorkoutStructure
{
    public const int MaxRepeat = 30;
    public const int MaxStrides = 12;
    public const double MaxStrideMinutes = 0.5;
    public const double MaxStrideKm = 0.2;
    private const double MinFlexibleKm = 1;

    private static readonly Intensity[] RestIntensities = [Intensity.Recovery, Intensity.Easy, Intensity.Steady];

    private static readonly Dictionary<WorkoutType, Intensity[]> Allowed = new()
    {
        [WorkoutType.Recovery] = [Intensity.Recovery, Intensity.Easy],
        [WorkoutType.Easy] = [Intensity.Recovery, Intensity.Easy, Intensity.Strides],
        [WorkoutType.Long] = [Intensity.Recovery, Intensity.Easy, Intensity.Steady, Intensity.Marathon, Intensity.HalfMarathon],
        [WorkoutType.Tempo] = [Intensity.Recovery, Intensity.Easy, Intensity.Steady, Intensity.Marathon, Intensity.HalfMarathon,
            Intensity.Threshold, Intensity.TenK, Intensity.Strides],
        [WorkoutType.Interval] = [Intensity.Recovery, Intensity.Easy, Intensity.Steady, Intensity.Threshold, Intensity.TenK,
            Intensity.FiveK, Intensity.Repetition, Intensity.Hill, Intensity.Strides],
        [WorkoutType.RacePace] = [Intensity.Recovery, Intensity.Easy, Intensity.Steady, Intensity.Marathon, Intensity.HalfMarathon,
            Intensity.Threshold, Intensity.TenK, Intensity.FiveK, Intensity.Strides],
    };

    public static IReadOnlyList<WorkoutBlock>? Parse(string? json) => CoachJson.Deserialize<List<WorkoutBlock>>(json);

    public static string? Serialize(IReadOnlyList<WorkoutBlock>? blocks) => blocks is { Count: > 0 } ? CoachJson.Serialize(blocks) : null;

    public static IntensityBand Band(Intensity intensity) => intensity switch
    {
        Intensity.Marathon or Intensity.HalfMarathon => IntensityBand.RacePace,
        Intensity.Threshold or Intensity.TenK => IntensityBand.Threshold,
        Intensity.FiveK or Intensity.Hill => IntensityBand.Vo2,
        Intensity.Repetition or Intensity.Strides => IntensityBand.Repetition,
        _ => IntensityBand.None,
    };

    /// <summary>How much of a km at this intensity counts as hard running (steady counts half).</summary>
    public static double HardWeight(Intensity intensity) => intensity switch
    {
        Intensity.Recovery or Intensity.Easy => 0,
        Intensity.Steady => 0.5,
        _ => 1,
    };

    public static double PartKm(StepPart part, TrainingZones zones) =>
        part.Km is double km ? km : part.Minutes is double minutes ? minutes * 60 / zones.ConversionPaceFor(part.Intensity) : 0;

    public static double TotalKm(IReadOnlyList<WorkoutBlock> blocks, TrainingZones zones) =>
        blocks.Sum(b => b.Repeat * (PartKm(b.Work, zones) + (b.Rest is null ? 0 : PartKm(b.Rest, zones))));

    public static Dictionary<Intensity, double> KmByIntensity(IReadOnlyList<WorkoutBlock> blocks, TrainingZones zones)
    {
        var result = new Dictionary<Intensity, double>();
        foreach (var b in blocks)
        {
            foreach (var part in b.Rest is null ? [b.Work] : new[] { b.Work, b.Rest })
            {
                result[part.Intensity] = result.GetValueOrDefault(part.Intensity) + b.Repeat * PartKm(part, zones);
            }
        }

        return result;
    }

    /// <summary>Km run above easy effort. Sessions without steps use the typical share for their type.</summary>
    public static double HardKm(PlannedWorkout w, TrainingZones zones)
    {
        var blocks = Parse(w.StepsJson);
        return blocks is { Count: > 0 }
            ? KmByIntensity(blocks, zones).Sum(kv => HardWeight(kv.Key) * kv.Value)
            : w.Type.HardShare() * w.TargetDistanceKm;
    }

    /// <summary>Km per capped intensity band in one session. Sessions without steps are attributed by type.</summary>
    public static Dictionary<IntensityBand, double> BandKm(PlannedWorkout w, TrainingZones zones)
    {
        var blocks = Parse(w.StepsJson);
        if (blocks is not { Count: > 0 })
        {
            var band = DefaultBand(w.Type, zones);
            return band == IntensityBand.None ? [] : new() { [band] = w.Type.HardShare() * w.TargetDistanceKm };
        }

        return KmByIntensity(blocks, zones)
            .Where(kv => Band(kv.Key) != IntensityBand.None)
            .GroupBy(kv => Band(kv.Key))
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
    }

    /// <summary>The band a session without steps is counted in (its hard share, <see cref="WorkoutTypeExtensions.HardShare"/>).</summary>
    public static IntensityBand DefaultBand(WorkoutType type, TrainingZones zones) => type switch
    {
        WorkoutType.Tempo => IntensityBand.Threshold,
        WorkoutType.Interval => IntensityBand.Vo2,
        WorkoutType.RacePace => Band(zones.RaceIntensity),
        _ => IntensityBand.None,
    };

    public static IntensityCap CapFor(this SafetyOptions options, IntensityBand band) => band switch
    {
        IntensityBand.Threshold => options.ThresholdCap,
        IntensityBand.Vo2 => options.Vo2Cap,
        IntensityBand.Repetition => options.RepetitionCap,
        _ => options.RacePaceCap,
    };

    /// <summary>The hardest intensity of the work parts; it sets the headline pace of a quality session.</summary>
    public static Intensity? MainIntensity(IReadOnlyList<WorkoutBlock> blocks) =>
        blocks.Select(b => b.Work.Intensity).Where(i => i is not (Intensity.Strides or Intensity.Easy or Intensity.Recovery))
            .Cast<Intensity?>().OrderByDescending(i => Rank(i!.Value)).FirstOrDefault();

    /// <summary>True when a session is meant to be run at one steady easy effort, so its average pace can be judged.</summary>
    public static bool IsSteadyEasy(PlannedWorkout w) =>
        w.Type is WorkoutType.Easy or WorkoutType.Recovery or WorkoutType.Long
        && (Parse(w.StepsJson) ?? []).All(b => HardWeight(b.Work.Intensity) == 0 && (b.Rest is null || HardWeight(b.Rest.Intensity) == 0));

    /// <summary>Checks that a structure is well-formed and matches its session type.</summary>
    public static IReadOnlyList<StructureProblem> Problems(WorkoutType type, IReadOnlyList<WorkoutBlock> blocks, Intensity raceIntensity)
    {
        var problems = new List<StructureProblem>();
        if (blocks.Count == 0)
        {
            return problems;
        }

        if (!Allowed.TryGetValue(type, out var allowed))
        {
            problems.Add(new("steps_not_allowed", type.ToString()));
            return problems;
        }

        var strides = 0;
        foreach (var b in blocks)
        {
            if (b.Repeat is < 1 or > MaxRepeat || !WellFormed(b.Work) || (b.Rest is not null && !WellFormed(b.Rest)))
            {
                problems.Add(new("steps_invalid", $"{b.Repeat}x {b.Work.Intensity}"));
                continue;
            }

            if (b.Rest is not null && !RestIntensities.Contains(b.Rest.Intensity))
            {
                problems.Add(new("steps_invalid", $"recovery at {b.Rest.Intensity}"));
            }

            foreach (var intensity in b.Rest is null ? [b.Work.Intensity] : new[] { b.Work.Intensity, b.Rest.Intensity })
            {
                if (!allowed.Contains(intensity))
                {
                    // Hard work hidden in an easy run defeats every load check keyed on session type; in a quality
                    // session an unusual intensity is a coaching choice.
                    problems.Add(type is WorkoutType.Easy or WorkoutType.Recovery
                        ? new("steps_intensity_not_allowed", $"{intensity} in {type}")
                        : new("steps_intensity_unusual", $"{intensity} in {type}"));
                }
            }

            if (b.Work.Intensity == Intensity.Strides)
            {
                strides += b.Repeat;
                if (b.Work.Minutes > MaxStrideMinutes || b.Work.Km > MaxStrideKm)
                {
                    problems.Add(new("strides_too_long", "strides are short accelerations (<= 30 s or 200 m)"));
                }
            }
        }

        if (strides > MaxStrides)
        {
            problems.Add(new("strides_too_many", $"{strides} > {MaxStrides}"));
        }

        Intensity[] main = type switch
        {
            WorkoutType.Tempo => [Intensity.HalfMarathon, Intensity.Threshold, Intensity.TenK],
            WorkoutType.Interval => [Intensity.TenK, Intensity.FiveK, Intensity.Repetition, Intensity.Hill],
            WorkoutType.RacePace => [raceIntensity],
            _ => [],
        };
        if (main.Length > 0 && !blocks.Any(b => main.Contains(b.Work.Intensity)))
        {
            problems.Add(new("steps_missing_main_set", $"{type} needs work at {string.Join("/", main)}"));
        }

        return problems;
    }

    /// <summary>Fills in each part's pace range from measured fitness.</summary>
    public static List<WorkoutBlock> Resolve(IReadOnlyList<WorkoutBlock> blocks, TrainingZones zones) =>
        [.. blocks.Select(b => b with { Work = Resolve(b.Work, zones), Rest = b.Rest is null ? null : Resolve(b.Rest, zones) })];

    /// <summary>Scales every pace by <paramref name="ratio"/> (after a fitness change).</summary>
    public static List<WorkoutBlock> ScalePaces(IReadOnlyList<WorkoutBlock> blocks, double ratio) =>
        [.. blocks.Select(b => b with { Work = Scale(b.Work, ratio), Rest = b.Rest is null ? null : Scale(b.Rest, ratio) })];

    /// <summary>
    /// Fits a structure to a new total distance. Easy single blocks (warm-up, cool-down) absorb the difference first, down
    /// to 1 km each; then repeats of the main set are dropped. Returns null when it cannot be fitted, in which case the
    /// workout becomes a plain run of its type.
    /// </summary>
    public static List<WorkoutBlock>? Resize(IReadOnlyList<WorkoutBlock> blocks, double targetKm, TrainingZones zones)
    {
        var list = blocks.ToList();
        for (var guard = 0; guard < 200; guard++)
        {
            var delta = targetKm - TotalKm(list, zones);
            if (Math.Abs(delta) < 0.25)
            {
                return list;
            }

            var flexible = Enumerable.Range(0, list.Count)
                .Where(i => list[i].Repeat == 1 && list[i].Rest is null && list[i].Work.Intensity is Intensity.Easy or Intensity.Recovery)
                .ToList();

            if (delta > 0)
            {
                if (flexible.Count == 0)
                {
                    list.Add(new WorkoutBlock(1, Resolve(new StepPart(Intensity.Easy, Km: Math.Round(delta * 2) / 2), zones)));
                }
                else
                {
                    var i = flexible[^1];
                    list[i] = list[i] with { Work = list[i].Work with { Km = PartKm(list[i].Work, zones) + delta, Minutes = null } };
                }

                continue;
            }

            var shrunk = false;
            foreach (var i in Enumerable.Reverse(flexible))
            {
                var km = PartKm(list[i].Work, zones);
                var cut = Math.Min(-delta, km - MinFlexibleKm);
                if (cut > 0.05)
                {
                    list[i] = list[i] with { Work = list[i].Work with { Km = km - cut, Minutes = null } };
                    shrunk = true;
                    break;
                }
            }

            if (shrunk)
            {
                continue;
            }

            var repeated = Enumerable.Range(0, list.Count).Where(i => list[i].Repeat > 1)
                .OrderByDescending(i => PartKm(list[i].Work, zones) + (list[i].Rest is null ? 0 : PartKm(list[i].Rest!, zones)))
                .Cast<int?>().FirstOrDefault();
            if (repeated is not int r)
            {
                return null;
            }

            list[r] = list[r] with { Repeat = list[r].Repeat - 1 };
        }

        return null;
    }

    private static int Rank(Intensity i) => i switch
    {
        Intensity.Steady => 1,
        Intensity.Marathon => 2,
        Intensity.HalfMarathon => 3,
        Intensity.Threshold => 4,
        Intensity.TenK => 5,
        Intensity.Hill => 6,
        Intensity.FiveK => 7,
        Intensity.Repetition => 8,
        _ => 0,
    };

    private static bool WellFormed(StepPart p) =>
        (p.Km, p.Minutes) switch
        {
            (double km, null) => km is > 0 and <= 50,
            (null, double minutes) => minutes is > 0 and <= 300,
            _ => false,
        };

    private static StepPart Resolve(StepPart p, TrainingZones zones)
    {
        var pace = zones.PaceFor(p.Intensity);
        return p with { PaceFast = pace?.Fast, PaceSlow = pace?.Slow };
    }

    private static StepPart Scale(StepPart p, double ratio) => p with
    {
        PaceFast = p.PaceFast is int f ? (int)Math.Round(f * ratio) : null,
        PaceSlow = p.PaceSlow is int s ? (int)Math.Round(s * ratio) : null,
    };
}
