using CoachBot.Core.Domain;
using CoachBot.Core.Services;

namespace CoachBot.Core.Planning;

/// <summary>
/// One part of a block: a distance or a duration at one intensity. The pace range is looked up in the plan's
/// <see cref="PaceTable"/> when the workout is created (null when the table has no entry: run by effort).
/// </summary>
public sealed record StepPart(Intensity Intensity, double? Km = null, double? Minutes = null, int? PaceFast = null, int? PaceSlow = null);

/// <summary>A block of a structured workout, run <see cref="Repeat"/> times: the work part, then an optional recovery part.</summary>
public sealed record WorkoutBlock(int Repeat, StepPart Work, StepPart? Rest = null);

public sealed record StructureProblem(string Code, string Detail);

/// <summary>
/// Stores and measures structured workouts. What a session contains is the coach's choice; code only checks that a
/// structure can be stored and measured.
/// </summary>
public static class WorkoutStructure
{
    public const int MaxRepeat = 30;

    public static IReadOnlyList<WorkoutBlock>? Parse(string? json) => CoachJson.Deserialize<List<WorkoutBlock>>(json);

    public static string? Serialize(IReadOnlyList<WorkoutBlock>? blocks) => blocks is { Count: > 0 } ? CoachJson.Serialize(blocks) : null;

    public static double PartKm(StepPart part, PaceTable paces) =>
        part.Km is double km ? km : part.Minutes is double minutes ? minutes * 60 / paces.ConversionPaceSecPerKm(part.Intensity) : 0;

    public static double TotalKm(IReadOnlyList<WorkoutBlock> blocks, PaceTable paces) =>
        blocks.Sum(b => b.Repeat * (PartKm(b.Work, paces) + (b.Rest is null ? 0 : PartKm(b.Rest, paces))));

    /// <summary>The hardest intensity of the work parts; it sets the headline pace of a quality session.</summary>
    public static Intensity? MainIntensity(IReadOnlyList<WorkoutBlock> blocks) =>
        blocks.Select(b => b.Work.Intensity).Where(i => i is not (Intensity.Strides or Intensity.Easy or Intensity.Recovery))
            .Cast<Intensity?>().OrderByDescending(i => Rank(i!.Value)).FirstOrDefault();

    /// <summary>Checks that a structure can be stored and measured: a repeat count in range, and each part a distance or a duration.</summary>
    public static IReadOnlyList<StructureProblem> Problems(IReadOnlyList<WorkoutBlock> blocks) =>
        [.. blocks
            .Where(b => b.Repeat is < 1 or > MaxRepeat || !WellFormed(b.Work) || (b.Rest is not null && !WellFormed(b.Rest)))
            .Select(b => new StructureProblem("steps_invalid", $"{b.Repeat}x {b.Work.Intensity}: repeat 1..{MaxRepeat}, each part either km (0–50] or minutes (0–300]"))];

    /// <summary>Fills in each part's pace range from the plan's pace table.</summary>
    public static List<WorkoutBlock> Resolve(IReadOnlyList<WorkoutBlock> blocks, PaceTable paces) =>
        [.. blocks.Select(b => b with { Work = Resolve(b.Work, paces), Rest = b.Rest is null ? null : Resolve(b.Rest, paces) })];

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

    private static StepPart Resolve(StepPart p, PaceTable paces)
    {
        var pace = paces.PaceFor(p.Intensity);
        return p with { PaceFast = pace?.Fast, PaceSlow = pace?.Slow };
    }
}
