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
/// Effort of one part of a structured workout. Paces and heart-rate targets for each are derived from measured fitness
/// (<see cref="Planning.TrainingZones"/>); the LLM only chooses the intensity.
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

/// <summary>Race distances the safety limits are tuned for. Other distances use the closest profile.</summary>
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

public enum ChangeAction
{
    ReduceDistance,
    Downgrade,
    ConvertToRest,
    SwapDays,
    IncreaseDistance,
    Upgrade,
    ChangePace,
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
    /// <summary>Sessions that count as "hard" for spacing rules (no two hard days back-to-back).</summary>
    public static bool IsHard(this WorkoutType type) =>
        type is WorkoutType.Long or WorkoutType.Tempo or WorkoutType.Interval or WorkoutType.RacePace or WorkoutType.Race;

    /// <summary>Quality sessions are the intensity workouts capped per week.</summary>
    public static bool IsQuality(this WorkoutType type) =>
        type is WorkoutType.Tempo or WorkoutType.Interval or WorkoutType.RacePace;

    public static bool IsRun(this WorkoutType type) => type != WorkoutType.Rest;

    /// <summary>
    /// Share of a session's distance spent above easy intensity (warm-up/cool-down excluded), for sessions without a
    /// structure. Structured sessions are measured from their steps (<see cref="Planning.WorkoutStructure"/>).
    /// </summary>
    public static double HardShare(this WorkoutType type) => type switch
    {
        WorkoutType.Tempo => 0.5,
        WorkoutType.Interval => 0.4,
        WorkoutType.RacePace => 0.6,
        WorkoutType.Race => 1.0,
        _ => 0.0,
    };

    /// <summary>Relative intensity rank used to decide whether a change is a downgrade or an upgrade.</summary>
    public static int IntensityRank(this WorkoutType type) => type switch
    {
        WorkoutType.Rest => 0,
        WorkoutType.Recovery => 1,
        WorkoutType.Easy => 2,
        WorkoutType.Long => 3,
        WorkoutType.RacePace => 4,
        WorkoutType.Tempo => 5,
        WorkoutType.Interval => 6,
        WorkoutType.Race => 7,
        _ => 0,
    };
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
