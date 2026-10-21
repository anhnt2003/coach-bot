using CoachBot.Core.Domain;

namespace CoachBot.Core.Planning;

/// <summary>Pace range in seconds per km. <see cref="Fast"/> is the lower (quicker) bound.</summary>
public sealed record PaceRange(int Fast, int Slow);

public sealed record HrRange(int Min, int Max);

public static class ZoneBasis
{
    public const string CorosThreshold = "coros_threshold";
    public const string CorosPrediction = "coros_marathon_prediction";
    public const string RecentRuns = "estimated_from_runs";
    public const string HeartRate = "heart_rate";
    public const string Rpe = "rpe";
}

/// <summary>
/// Pace and heart-rate targets derived from measured fitness. Every intensity is anchored on the threshold pace; the
/// goal race pace replaces the predicted one when the goal is realistic.
/// </summary>
public sealed record TrainingZones(
    string Basis,
    int? ThresholdPaceSecPerKm,
    int? MarathonPaceSecPerKm,
    int? MaxHr,
    double RaceDistanceKm = RaceProfiles.MarathonKm,
    int? RacePaceSecPerKm = null)
{
    public bool HasPace => ThresholdPaceSecPerKm is not null;

    /// <summary>Goal distance; plans stored before goal distances existed were all marathons.</summary>
    public double RaceKm => RaceDistanceKm > 0 ? RaceDistanceKm : RaceProfiles.MarathonKm;

    public Intensity RaceIntensity => RaceProfiles.RaceIntensity(RaceKm);

    public PaceRange? PaceFor(WorkoutType type) => type switch
    {
        WorkoutType.Recovery => PaceFor(Intensity.Recovery),
        WorkoutType.Easy => PaceFor(Intensity.Easy),
        WorkoutType.Long when ThresholdPaceSecPerKm is int t => Scale(t, 1.15, 1.28),
        WorkoutType.Tempo => PaceFor(Intensity.Threshold),
        WorkoutType.Interval when ThresholdPaceSecPerKm is int t => Scale(t, 0.90, 0.95),
        WorkoutType.RacePace or WorkoutType.Race => PaceFor(RaceIntensity),
        _ => null,
    };

    /// <summary>Target pace for one part of a structured workout. Hills and strides are run by effort (null).</summary>
    public PaceRange? PaceFor(Intensity intensity)
    {
        if (ThresholdPaceSecPerKm is not int t)
        {
            return null;
        }

        return intensity switch
        {
            Intensity.Recovery => Scale(t, 1.30, 1.42),
            Intensity.Easy => Scale(t, 1.18, 1.32),
            Intensity.Steady => Scale(t, 1.09, 1.15),
            Intensity.Marathon or Intensity.HalfMarathon or Intensity.TenK or Intensity.FiveK => Scale(CenterPace(intensity, t), 0.98, 1.02),
            Intensity.Threshold => Scale(t, 0.98, 1.03),
            Intensity.Repetition => Scale(t, 0.84, 0.89),
            _ => null,
        };
    }

    public HrRange? HrFor(WorkoutType type)
    {
        if (MaxHr is not int max)
        {
            return null;
        }

        return type switch
        {
            WorkoutType.Recovery => HrFor(Intensity.Recovery),
            WorkoutType.Easy => HrFor(Intensity.Easy),
            WorkoutType.Long => Pct(max, 0.65, 0.80),
            WorkoutType.Tempo => HrFor(Intensity.Threshold),
            WorkoutType.Interval => Pct(max, 0.90, 0.95),
            WorkoutType.RacePace or WorkoutType.Race => HrFor(RaceIntensity),
            _ => null,
        };
    }

    /// <summary>Heart-rate range for an intensity; null for efforts too short for heart rate to be useful.</summary>
    public HrRange? HrFor(Intensity intensity)
    {
        if (MaxHr is not int max)
        {
            return null;
        }

        return intensity switch
        {
            Intensity.Recovery => Pct(max, 0.60, 0.70),
            Intensity.Easy => Pct(max, 0.65, 0.78),
            Intensity.Steady => Pct(max, 0.75, 0.83),
            Intensity.Marathon => Pct(max, 0.78, 0.86),
            Intensity.HalfMarathon => Pct(max, 0.83, 0.89),
            Intensity.Threshold => Pct(max, 0.85, 0.90),
            Intensity.TenK => Pct(max, 0.88, 0.92),
            Intensity.FiveK => Pct(max, 0.90, 0.95),
            _ => null,
        };
    }

    /// <summary>Slowest plausible pace for a session type; used to bound long-run duration.</summary>
    public int SlowestPaceFor(WorkoutType type) => PaceFor(type)?.Slow ?? DefaultSlowPaceSecPerKm;

    /// <summary>
    /// Pace used to turn a timed step into distance. Without pace data a fairly quick 5:30/km threshold is assumed, which
    /// overestimates distance (the safe direction for volume and intensity limits).
    /// </summary>
    public int ConversionPaceFor(Intensity intensity)
    {
        var zones = HasPace ? this : this with { ThresholdPaceSecPerKm = NominalThresholdSecPerKm, MarathonPaceSecPerKm = null, RacePaceSecPerKm = null };
        var effective = intensity switch
        {
            Intensity.Hill => Intensity.Threshold,
            Intensity.Strides => Intensity.Repetition,
            _ => intensity,
        };
        var pace = zones.PaceFor(effective)!;
        return (pace.Fast + pace.Slow) / 2;
    }

    /// <summary>Conservative 7:00/km assumption when no pace data exists.</summary>
    public const int DefaultSlowPaceSecPerKm = 420;

    private const int NominalThresholdSecPerKm = 330;

    private int CenterPace(Intensity intensity, int threshold)
    {
        if (intensity == RaceIntensity && RacePaceSecPerKm is int goal)
        {
            return goal;
        }

        return intensity switch
        {
            Intensity.Marathon => MarathonPaceSecPerKm ?? PaceZoneCalculator.PredictedRacePace(threshold, RaceProfiles.MarathonKm),
            Intensity.HalfMarathon => PaceZoneCalculator.PredictedRacePace(threshold, RaceProfiles.HalfKm),
            Intensity.TenK => PaceZoneCalculator.PredictedRacePace(threshold, RaceProfiles.TenKKm),
            _ => PaceZoneCalculator.PredictedRacePace(threshold, RaceProfiles.FiveKKm),
        };
    }

    private static PaceRange Scale(int pace, double fast, double slow) =>
        new((int)Math.Round(pace * fast), (int)Math.Round(pace * slow));

    private static HrRange Pct(int max, double lo, double hi) =>
        new((int)Math.Round(max * lo), (int)Math.Round(max * hi));
}

public sealed record RecentEffort(double DistanceKm, int DurationSeconds);

public sealed record FitnessInputs(
    int? CorosThresholdPaceSecPerKm,
    int? CorosMarathonPredictionSeconds,
    IReadOnlyList<RecentEffort> RecentEfforts,
    int? GoalTimeSeconds,
    double RaceDistanceKm,
    int? MaxHr,
    int? BirthYear,
    int CurrentYear);

public sealed record ZoneResult(TrainingZones Zones, IReadOnlyList<string> Warnings);

/// <summary>
/// Derives training zones with a fallback chain: COROS threshold pace → COROS marathon prediction →
/// recent training runs (Riegel) → heart rate → RPE only. Missing data is never filled with guesses that
/// would make targets more aggressive.
/// </summary>
public static class PaceZoneCalculator
{
    private const double MarathonKm = RaceProfiles.MarathonKm;
    private const double HalfKm = RaceProfiles.HalfKm;

    public static ZoneResult Calculate(FitnessInputs input)
    {
        var warnings = new List<string>();
        int? threshold = null;
        string basis;

        if (input.CorosThresholdPaceSecPerKm is > 150 and < 900)
        {
            threshold = input.CorosThresholdPaceSecPerKm;
            basis = ZoneBasis.CorosThreshold;
        }
        else if (input.CorosMarathonPredictionSeconds is > 7200 and < 30000)
        {
            threshold = (int)Math.Round(input.CorosMarathonPredictionSeconds.Value / MarathonKm / 1.07);
            basis = ZoneBasis.CorosPrediction;
        }
        else if (EstimateThresholdFromEfforts(input.RecentEfforts) is int estimated)
        {
            threshold = estimated;
            basis = ZoneBasis.RecentRuns;
            warnings.Add("zones.estimated_from_runs");
        }
        else
        {
            basis = ZoneBasis.Rpe;
        }

        var maxHr = input.MaxHr;
        if (maxHr is null && input.BirthYear is int birthYear && birthYear > 1900)
        {
            // Tanaka formula; individual variation is large, so flag it.
            maxHr = (int)Math.Round(208 - 0.7 * (input.CurrentYear - birthYear));
            warnings.Add("zones.max_hr_estimated");
        }

        if (threshold is null && maxHr is not null)
        {
            basis = ZoneBasis.HeartRate;
        }

        if (threshold is null && maxHr is null)
        {
            warnings.Add("zones.rpe_only");
        }

        var raceKm = input.RaceDistanceKm > 0 ? input.RaceDistanceKm : MarathonKm;
        int? marathonPace = null;
        int? racePace = null;
        if (threshold is int t)
        {
            marathonPace = PredictedRacePace(t, MarathonKm);
            racePace = PredictedRacePace(t, raceKm);
            if (input.GoalTimeSeconds is int goal)
            {
                var goalPace = (int)Math.Round(goal / raceKm);
                if (goalPace < racePace * 0.95)
                {
                    // Goal is >5% faster than current fitness supports: train at current fitness, not the goal.
                    warnings.Add("zones.goal_too_ambitious");
                }
                else
                {
                    racePace = goalPace;
                    if (RaceProfiles.RaceIntensity(raceKm) == Intensity.Marathon)
                    {
                        marathonPace = goalPace;
                    }
                }
            }
        }

        return new ZoneResult(new TrainingZones(basis, threshold, marathonPace, maxHr, raceKm, racePace), warnings);
    }

    /// <summary>
    /// Race pace current fitness predicts for a distance: marathon pace ≈ 107% of threshold pace, half-marathon pace
    /// ≈ threshold / 0.97, other distances by Riegel's formula (exponent 1.06) from the nearest of the two.
    /// </summary>
    public static int PredictedRacePace(int thresholdSecPerKm, double distanceKm) =>
        (int)Math.Round(distanceKm >= 30
            ? thresholdSecPerKm * 1.07 * Math.Pow(distanceKm / MarathonKm, 0.06)
            : thresholdSecPerKm / 0.97 * Math.Pow(distanceKm / HalfKm, 0.06));

    /// <summary>Predicted finish time for a distance from current fitness; null without pace data.</summary>
    public static int? PredictedRaceSeconds(TrainingZones zones) =>
        zones.ThresholdPaceSecPerKm is int t ? (int)Math.Round(PredictedRacePace(t, zones.RaceKm) * zones.RaceKm) : null;

    /// <summary>
    /// Riegel projection of the best recent effort to a half marathon; threshold ≈ 97% of HM pace.
    /// Training runs are usually sub-maximal, so this errs slow (safe).
    /// </summary>
    internal static int? EstimateThresholdFromEfforts(IReadOnlyList<RecentEffort> efforts)
    {
        var best = efforts
            .Where(e => e.DistanceKm >= 5 && e.DurationSeconds >= 1200)
            .Select(e => e.DurationSeconds * Math.Pow(HalfKm / e.DistanceKm, 1.06))
            .DefaultIfEmpty(double.NaN)
            .Min();

        if (double.IsNaN(best))
        {
            return null;
        }

        var threshold = (int)Math.Round(best / HalfKm * 0.97);
        return threshold is > 150 and < 900 ? threshold : null;
    }
}
