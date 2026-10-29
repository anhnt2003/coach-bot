using System.ComponentModel.DataAnnotations;
using CoachBot.Core.Domain;

namespace CoachBot.Core.Abstractions;

public sealed class CoachOptions
{
    public const string Section = "Coach";

    [Required]
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";

    [RegularExpression("^(en|vi)$")]
    public string Language { get; set; } = "en";

    /// <summary>Public URL of this service; only needed for the COROS PKCE redirect fallback.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>How many days ahead approved workouts are pushed to COROS.</summary>
    [Range(1, 14)]
    public int PushWindowDays { get; set; } = 7;
}

/// <summary>
/// Deterministic safety limits. Defaults are conservative. The distance-specific values below are for the marathon;
/// <see cref="ForRace"/> derives the limits for shorter goal races.
/// </summary>
public sealed class SafetyOptions
{
    public const string Section = "Safety";

    public double MaxWeeklyIncreasePct { get; set; } = 0.10;
    public double MaxWeeklyIncreaseKm { get; set; } = 8;
    public int CutbackEveryWeeks { get; set; } = 4;
    public double CutbackFactor { get; set; } = 0.75;

    /// <summary>
    /// Share of weekly volume the generator lets the long run grow to. 30% (Hansons-style) left typical runners with
    /// ~17 km peak long runs, too short for marathon preparation; 45% sits between Pfitzinger and Higdon.
    /// </summary>
    public double LongRunTargetShare { get; set; } = 0.45;
    /// <summary>Most of the week the long run may be: a hard limit for the generator, a guideline for LLM designs.</summary>
    public double LongRunHardMaxShare { get; set; } = 0.50;
    public double LongRunCapKm { get; set; } = 32;
    public int LongRunCapMinutes { get; set; } = 195;
    public double LongRunMaxIncreaseKm { get; set; } = 3;

    // Coaching guidelines: the rule-based generator follows them, but for LLM designs and adaptations they are only
    // reported (Violation.IsGuideline): quality count, easy share, long-run share, per-session intensity caps.
    public int MaxQualityPerWeek { get; set; } = 2;
    public int MaxQualityPerWeekBase { get; set; } = 1;
    public double MinEasyShare { get; set; } = 0.75;
    public double[] TaperFactors { get; set; } = [0.8, 0.6, 0.4];
    /// <summary>Race-week volume (excluding the race) as a share of peak, at most.</summary>
    public double RaceWeekMaxShare { get; set; } = 0.6;
    /// <summary>Volume of the week before race week as a share of peak, at most (plans of 8+ weeks).</summary>
    public double PreRaceWeekMaxShare { get; set; } = 0.85;

    public int MinWeeksRecommended { get; set; } = 12;
    public int MinWeeksAbsolute { get; set; } = 8;
    public double MinBaseKmForShortBuild { get; set; } = 40;
    public double MinLongRunKmForShortBuild { get; set; } = 21;
    public double LowBaseWarningKm { get; set; } = 20;
    /// <summary>Peak weekly volume = max(base, min(cap, max(floor, base × multiplier))).</summary>
    public double PeakVolumeCapKm { get; set; } = 90;
    public double PeakFloorKm { get; set; } = 50;
    public double PeakBaseMultiplier { get; set; } = 1.8;

    // Per-session limits on running at each intensity (Daniels' guidelines): share of the week's volume, never more
    // than MaxKm, but always allowing FloorKm so low-volume runners can still do a meaningful session.
    public IntensityCap ThresholdCap { get; set; } = new() { Share = 0.10, FloorKm = 4, MaxKm = 15 };
    public IntensityCap Vo2Cap { get; set; } = new() { Share = 0.08, FloorKm = 3, MaxKm = 10 };
    public IntensityCap RepetitionCap { get; set; } = new() { Share = 0.05, FloorKm = 1.6, MaxKm = 8 };
    public IntensityCap RacePaceCap { get; set; } = new() { Share = 0.20, FloorKm = 6, MaxKm = 29 };

    public int ApprovalExpiryHours { get; set; } = 48;
    public int MinorChangeHorizonDays { get; set; } = 3;
    public double MinorMaxReduction { get; set; } = 0.30;
    public double LongRunMajorChangeShare { get; set; } = 0.20;

    /// <summary>
    /// Limits for a goal race of <paramref name="distanceKm"/>. Shorter races get smaller long-run and volume ceilings,
    /// a shorter taper and shorter minimum preparation. Those ceilings never exceed the configured (marathon) values, so
    /// lowering a marathon limit lowers it for every distance. The race-week shares are the exception: a shorter race
    /// tapers less, so its race week may keep more of the peak than the configured marathon share.
    /// </summary>
    public SafetyOptions ForRace(double distanceKm)
    {
        var profile = RaceProfiles.For(distanceKm);
        if (profile == RaceProfile.Marathon)
        {
            return this;
        }

        var (longShare, longKm, longMin, peakCap, peakFloor, peakMult, taper, raceWeek, preRace, minWeeks, recWeeks, shortBase, shortLong, lowBase) = profile switch
        {
            RaceProfile.Half => (0.40, 22.0, 150, 70.0, 40.0, 1.6, new[] { 0.8, 0.55 }, 0.6, 0.85, 6, 10, 30.0, 15.0, 15.0),
            RaceProfile.TenK => (0.35, 18.0, 120, 65.0, 35.0, 1.5, new[] { 0.85, 0.65 }, 0.7, 0.9, 4, 8, 20.0, 10.0, 12.0),
            _ => (0.30, 16.0, 105, 60.0, 30.0, 1.5, new[] { 0.7 }, 0.75, 1.0, 4, 6, 15.0, 8.0, 10.0),
        };

        var o = (SafetyOptions)MemberwiseClone();
        o.LongRunTargetShare = Math.Min(LongRunTargetShare, longShare);
        o.LongRunCapKm = Math.Min(LongRunCapKm, longKm);
        o.LongRunCapMinutes = Math.Min(LongRunCapMinutes, longMin);
        o.PeakVolumeCapKm = Math.Min(PeakVolumeCapKm, peakCap);
        o.PeakFloorKm = Math.Min(PeakFloorKm, peakFloor);
        o.PeakBaseMultiplier = Math.Min(PeakBaseMultiplier, peakMult);
        o.TaperFactors = taper;
        o.RaceWeekMaxShare = Math.Max(RaceWeekMaxShare, raceWeek);
        o.PreRaceWeekMaxShare = Math.Max(PreRaceWeekMaxShare, preRace);
        o.MinWeeksAbsolute = Math.Min(MinWeeksAbsolute, minWeeks);
        o.MinWeeksRecommended = Math.Min(MinWeeksRecommended, recWeeks);
        o.MinBaseKmForShortBuild = Math.Min(MinBaseKmForShortBuild, shortBase);
        o.MinLongRunKmForShortBuild = Math.Min(MinLongRunKmForShortBuild, shortLong);
        o.LowBaseWarningKm = Math.Min(LowBaseWarningKm, lowBase);
        return o;
    }
}

public sealed class IntensityCap
{
    public double Share { get; set; }
    public double FloorKm { get; set; }
    public double MaxKm { get; set; }

    /// <summary>Most km at this intensity one session may contain in a week of <paramref name="weekKm"/>.</summary>
    public double LimitFor(double weekKm) => Math.Min(MaxKm, Math.Max(FloorKm, Share * weekKm));
}
