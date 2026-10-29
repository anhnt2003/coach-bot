namespace CoachBot.Core.Domain;

// Persistence entities. Mapped by EF Core onto the hand-written schema in supabase/migrations;
// any column change here must be matched by a new SQL migration.

public class Athlete
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Runner";
    public long? TelegramChatId { get; set; }
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";
    public string Language { get; set; } = "en";
    public int RunDaysPerWeek { get; set; } = 5;
    public DayOfWeek LongRunDay { get; set; } = DayOfWeek.Sunday;
    public int? MaxHr { get; set; }
    public int? BirthYear { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class RaceGoal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AthleteId { get; set; }
    public string Name { get; set; } = "Marathon";
    public DateOnly RaceDate { get; set; }
    public double DistanceKm { get; set; } = 42.195;
    public int? GoalTimeSeconds { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public class CorosConnection
{
    public Guid AthleteId { get; set; }
    public string McpUrl { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>Set when registered as a confidential client (client_secret_basic).</summary>
    public string? EncryptedClientSecret { get; set; }
    /// <summary>The redirect URI the client was registered with; null for clients registered before it was stored.</summary>
    public string? RedirectUri { get; set; }
    public string? EncryptedAccessToken { get; set; }
    public string? EncryptedRefreshToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    /// <summary>Tokens are stored; false before /connect, after /disconnect and once COROS rejects the refresh token.</summary>
    public bool IsConnected => EncryptedAccessToken is not null || EncryptedRefreshToken is not null;
    public string? Scopes { get; set; }
    /// <summary>JSON array of tool names returned by tools/list at the last verification.</summary>
    public string? CapabilitiesJson { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastSyncError { get; set; }
    public int ConsecutiveSyncFailures { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Activity
{
    /// <summary>Sport types that count as running; a static list so database queries can filter on it.</summary>
    public static readonly string[] RunSportTypes = ["run", "trail_run", "treadmill", "track"];

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AthleteId { get; set; }
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string SportType { get; set; } = "run";
    public string? Name { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public int DurationSeconds { get; set; }
    public double DistanceMeters { get; set; }
    public int? AvgPaceSecPerKm { get; set; }
    public int? AvgHr { get; set; }
    public int? MaxHr { get; set; }
    public double? ElevationGainM { get; set; }
    public int? AvgCadence { get; set; }
    public double? TrainingLoad { get; set; }
    public string? RawJson { get; set; }
    public DateTimeOffset ImportedAt { get; set; }

    public double DistanceKm => DistanceMeters / 1000.0;
    public bool IsRun => RunSportTypes.Contains(SportType);
}

public class DailyMetric
{
    public Guid AthleteId { get; set; }
    public DateOnly Date { get; set; }
    public int? RestingHr { get; set; }
    public double? HrvMs { get; set; }
    public int? SleepMinutes { get; set; }
    public int? SleepScore { get; set; }
    public int? StressAvg { get; set; }
    public int? RecoveryPct { get; set; }
    public double? AcuteLoad { get; set; }
    public double? ChronicLoad { get; set; }
    public double? LoadRatio { get; set; }
    public double? Vo2Max { get; set; }
    public int? ThresholdPaceSecPerKm { get; set; }
    public int? MarathonPredictionSeconds { get; set; }
    public string Source { get; set; } = "";
    public string? RawJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class TrainingPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AthleteId { get; set; }
    public Guid RaceGoalId { get; set; }
    public int Version { get; set; }
    public PlanStatus Status { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string GeneratedBy { get; set; } = "rules";
    public string? Rationale { get; set; }
    /// <summary>JSON array of warning message keys raised during generation.</summary>
    public string WarningsJson { get; set; } = "[]";
    /// <summary>JSON snapshot of the inputs (fitness, volume, zones) the plan was generated from.</summary>
    public string InputsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? PauseReason { get; set; }

    public List<PlannedWorkout> Workouts { get; set; } = [];
}

public class PlannedWorkout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public DateOnly Date { get; set; }
    public int WeekIndex { get; set; }
    public PlanPhase Phase { get; set; }
    public bool IsCutbackWeek { get; set; }
    public WorkoutType Type { get; set; }
    public double TargetDistanceKm { get; set; }
    public int? TargetDurationMinutes { get; set; }
    public int? PaceMinSecPerKm { get; set; }
    public int? PaceMaxSecPerKm { get; set; }
    public int? HrMin { get; set; }
    public int? HrMax { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>JSON array of <see cref="Planning.WorkoutBlock"/>; null for a plain run at one intensity.</summary>
    public string? StepsJson { get; set; }
    public WorkoutStatus Status { get; set; }
    public Guid? CompletedActivityId { get; set; }
    public SyncStatus CorosSyncStatus { get; set; }
    public string? CorosRef { get; set; }
    public string? CorosSyncError { get; set; }
    public DateTimeOffset? CorosSyncedAt { get; set; }
    /// <summary>Set when the workout changed after it was pushed, so the push job sends an update.</summary>
    public bool CorosDirty { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class RunAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AthleteId { get; set; }
    public Guid ActivityId { get; set; }
    public Guid? PlannedWorkoutId { get; set; }
    public double? DistanceRatio { get; set; }
    public string PaceStatus { get; set; } = "unknown";
    public string HrStatus { get; set; } = "unknown";
    public ReadinessLevel Readiness { get; set; }
    public Confidence ReadinessConfidence { get; set; }
    /// <summary>JSON: compliance flags and readiness signals.</summary>
    public string DetailsJson { get; set; } = "{}";
    public string? Narrative { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class AthleteFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AthleteId { get; set; }
    public Guid? ActivityId { get; set; }
    public DateOnly Date { get; set; }
    public int? Rpe { get; set; }
    public int? Soreness { get; set; }
    public string? Notes { get; set; }
    public SymptomCategory Symptom { get; set; }
    public bool RedFlag { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Recommendation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AthleteId { get; set; }
    public Guid PlanId { get; set; }
    public Guid? RunAnalysisId { get; set; }
    public string Trigger { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>JSON array of <see cref="WorkoutChange"/>.</summary>
    public string ChangesJson { get; set; } = "[]";
    public ChangeMagnitude Magnitude { get; set; }
    public RecommendationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public class PlanChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public Guid PlannedWorkoutId { get; set; }
    public Guid? RecommendationId { get; set; }
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
    public string Reason { get; set; } = "";
    public string AppliedBy { get; set; } = "";
    public DateTimeOffset AppliedAt { get; set; }
}

public class Alert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AthleteId { get; set; }
    public AlertType Type { get; set; }
    public AlertSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Prevents the same alert being re-sent, e.g. "fatigue:2026-10-21".</summary>
    public string? DedupKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}

public class JobRun
{
    public string JobName { get; set; } = "";
    public string RunKey { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
}

/// <summary>One call to the LLM, kept for debugging: the LLM makes every coaching decision and its answers do not repeat.</summary>
public class LlmCall
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? AthleteId { get; set; }
    public string Purpose { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>JSON: the context object the service gave the prompt.</summary>
    public string ContextJson { get; set; } = "{}";

    public string? Response { get; set; }

    /// <summary>ok | timeout | http | empty | schema | integrity</summary>
    public string Status { get; set; } = "ok";

    public string? Error { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The LLM's readiness verdict for one day, from the morning check or a run analysis.</summary>
public class ReadinessCheck
{
    public Guid AthleteId { get; set; }
    public DateOnly Date { get; set; }
    public ReadinessLevel Level { get; set; }
    public Confidence Confidence { get; set; }
    public string Summary { get; set; } = "";

    /// <summary>JSON array of strings.</summary>
    public string ConcernsJson { get; set; } = "[]";

    /// <summary>daily_check | run_analysis</summary>
    public string Source { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Everything a change can alter in a planned workout; the before/after of <see cref="PlanChange"/> and undo.</summary>
public sealed record WorkoutState(
    DateOnly Date, WorkoutType Type, double Km, int? DurationMinutes, int? PaceMin, int? PaceMax, int? HrMin, int? HrMax, string Title, string? Description,
    string? Steps = null);

/// <summary>
/// One change to one planned workout, as shown to the athlete: the state before and the complete state after, texts
/// included. Serialized into <see cref="Recommendation.ChangesJson"/>.
/// </summary>
public sealed record WorkoutChange(Guid WorkoutId, WorkoutState Before, WorkoutState After, string? Reason, bool NeedsApproval);
