using System.Text.Json.Serialization;
using CoachBot.Core.Domain;

namespace CoachBot.Core.Planning;

public sealed record PaceRange(int Fast, int Slow);

public sealed record HrRange(int Min, int Max);

/// <summary>The coach's pace (and optional heart-rate) target for one intensity.</summary>
public sealed record PaceEntry(Intensity Intensity, int PaceFastSecPerKm, int PaceSlowSecPerKm, int? HrMin = null, int? HrMax = null);

/// <summary>
/// Pace and heart-rate targets the LLM chose for the plan, per intensity. Code only looks targets up here; an intensity
/// without an entry is run by effort. Stored with the plan, so later changes use the same targets.
/// </summary>
public sealed record PaceTable(IReadOnlyList<PaceEntry> Entries, double RaceKm)
{
    /// <summary>Assumed pace for turning minutes into km when the table has no entry for an intensity.</summary>
    public const int DefaultConversionPaceSecPerKm = 420;

    public static PaceTable Empty(double raceKm) => new([], raceKm);

    /// <summary>What "race pace" means for the goal distance.</summary>
    [JsonIgnore]
    public Intensity RaceIntensity => RaceProfiles.RaceIntensity(RaceKm);

    public PaceRange? PaceFor(Intensity intensity) => Find(intensity) is { } e ? new(e.PaceFastSecPerKm, e.PaceSlowSecPerKm) : null;

    public HrRange? HrFor(Intensity intensity) => Find(intensity) is { HrMin: int min, HrMax: int max } ? new(min, max) : null;

    /// <summary>
    /// The intensity a plain session of this type is run at when the coach did not name one: a lookup, not a coaching
    /// choice (the coach names the intensity whenever it matters).
    /// </summary>
    public Intensity? DefaultIntensity(WorkoutType type) => type switch
    {
        WorkoutType.Recovery => Intensity.Recovery,
        WorkoutType.Easy or WorkoutType.Long => Intensity.Easy,
        WorkoutType.Tempo => Intensity.Threshold,
        WorkoutType.Interval => Intensity.FiveK,
        WorkoutType.RacePace or WorkoutType.Race => RaceIntensity,
        _ => null,
    };

    /// <summary>Middle of the pace range, for km ↔ minutes arithmetic.</summary>
    public int ConversionPaceSecPerKm(Intensity intensity) => PaceFor(intensity) is { } p ? (p.Fast + p.Slow) / 2 : DefaultConversionPaceSecPerKm;

    private PaceEntry? Find(Intensity intensity) => Entries.FirstOrDefault(e => e.Intensity == intensity);
}
