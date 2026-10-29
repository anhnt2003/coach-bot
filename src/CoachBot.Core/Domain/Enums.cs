namespace CoachBot.Core.Domain;

public enum WorkoutType
{
    Rest,
    Recovery,
    Easy,
    Long,
    Tempo,
    Interval,
    /// <summary>Work at goal race pace (marathon, half-marathon, 10K or 5K pace, depending on the goal).</summary>
    RacePace,
    Race,
}

/// <summary>
/// Effort of a plain run or of one part of a structured workout. The LLM sets the pace and heart-rate target for each in
/// the plan's <see cref="Planning.PaceTable"/>.
/// </summary>
public enum Intensity
{
    Recovery,
    Easy,
    Steady,
    Marathon,
    HalfMarathon,
    Threshold,
    TenK,
    FiveK,
    Repetition,
    Hill,
    Strides,
}

/// <summary>Standard race distances. Other distances use the closest profile.</summary>
public enum RaceProfile
{
    FiveK,
    TenK,
    Half,
    Marathon,
}

public enum WorkoutStatus
{
    Planned,
    Completed,
    Missed,
    Skipped,
}

public enum PlanStatus
{
    PendingApproval,
    Active,
    Paused,
    Superseded,
    Rejected,
    Completed,
}

public enum PlanPhase
{
    Base,
    Build,
    Peak,
    Taper,
}

public enum ReadinessLevel
{
    Green,
    Amber,
    Red,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

public enum ChangeMagnitude
{
    Minor,
    Major,
}

public enum RecommendationStatus
{
    Proposed,
    AutoApplied,
    Approved,
    Rejected,
    Expired,
    Undone,
    Informational,
}

public enum AlertType
{
    Fatigue,
    Symptom,
    MissingData,
    SyncFailure,
    CorosAuth,
    PushFailure,
    AnalysisFailure,
}

public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

public enum SyncStatus
{
    NotSynced,
    Synced,
    Failed,
    ManualActionRequired,
}

public enum SymptomCategory
{
    None,
    Illness,
    Injury,
    Cardiac,
}

public static class WorkoutTypeExtensions
{
    /// <summary>Intensity sessions, whose headline pace is that of their main set.</summary>
    public static bool IsQuality(this WorkoutType type) =>
        type is WorkoutType.Tempo or WorkoutType.Interval or WorkoutType.RacePace;
}

public static class RaceProfiles
{
    public const double FiveKKm = 5;
    public const double TenKKm = 10;
    public const double HalfKm = 21.0975;
    public const double MarathonKm = 42.195;

    public static RaceProfile For(double distanceKm) => distanceKm switch
    {
        < 7.5 => RaceProfile.FiveK,
        < 16 => RaceProfile.TenK,
        < 32 => RaceProfile.Half,
        _ => RaceProfile.Marathon,
    };

    /// <summary>The intensity that "race pace" means for a goal distance.</summary>
    public static Intensity RaceIntensity(double distanceKm) => For(distanceKm) switch
    {
        RaceProfile.FiveK => Intensity.FiveK,
        RaceProfile.TenK => Intensity.TenK,
        RaceProfile.Half => Intensity.HalfMarathon,
        _ => Intensity.Marathon,
    };

    public static string Key(RaceProfile profile) => profile switch
    {
        RaceProfile.FiveK => "5k",
        RaceProfile.TenK => "10k",
        RaceProfile.Half => "half",
        _ => "marathon",
    };
}
