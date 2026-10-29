using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CoachBot.Core.Abstractions;

public interface ICoachDb
{
    DbSet<Athlete> Athletes { get; }
    DbSet<RaceGoal> RaceGoals { get; }
    DbSet<CorosConnection> CorosConnections { get; }
    DbSet<Activity> Activities { get; }
    DbSet<DailyMetric> DailyMetrics { get; }
    DbSet<TrainingPlan> TrainingPlans { get; }
    DbSet<PlannedWorkout> PlannedWorkouts { get; }
    DbSet<RunAnalysis> RunAnalyses { get; }
    DbSet<AthleteFeedback> Feedback { get; }
    DbSet<Recommendation> Recommendations { get; }
    DbSet<PlanChange> PlanChanges { get; }
    DbSet<Alert> Alerts { get; }
    DbSet<JobRun> JobRuns { get; }
    DbSet<LlmCall> LlmCalls { get; }
    DbSet<ReadinessCheck> ReadinessChecks { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Source of training and health data, and target for pushing workouts.
/// Implemented by the official COROS MCP adapter.
/// </summary>
public interface ICorosGateway
{
    /// <summary>Value written to <see cref="Activity.Source"/> / <see cref="DailyMetric.Source"/>.</summary>
    string Source { get; }

    Task<CorosCapabilities> GetCapabilitiesAsync(Guid athleteId, CancellationToken ct);
    Task<IReadOnlyList<ActivitySample>> GetActivitiesAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<DailyMetricSample>> GetDailyMetricsAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<FitnessSnapshot?> GetFitnessAsync(Guid athleteId, CancellationToken ct);
    Task<WorkoutPushResult> PushScheduledWorkoutAsync(Guid athleteId, PlannedWorkout workout, CancellationToken ct);
}

/// <summary>The COROS session is missing, expired or revoked; the athlete has to reconnect.</summary>
public sealed class CorosAuthException(string message) : Exception(message);

public sealed record CorosCapabilities(IReadOnlySet<string> Tools, bool CanRead, bool CanPushWorkouts, bool CanUpdateWorkouts)
{
    public static CorosCapabilities None { get; } = new(new HashSet<string>(), false, false, false);
}

public sealed record ActivitySample(
    string ExternalId,
    string SportType,
    DateTimeOffset StartTime,
    int DurationSeconds,
    double DistanceMeters,
    int? AvgPaceSecPerKm,
    int? AvgHr,
    int? MaxHr,
    double? ElevationGainM,
    int? AvgCadence,
    double? TrainingLoad,
    string? Name,
    string? RawJson);

public sealed record DailyMetricSample(
    DateOnly Date,
    int? RestingHr = null,
    double? HrvMs = null,
    int? SleepMinutes = null,
    int? SleepScore = null,
    int? StressAvg = null,
    int? RecoveryPct = null,
    double? AcuteLoad = null,
    double? ChronicLoad = null,
    double? LoadRatio = null,
    string? RawJson = null);

public sealed record FitnessSnapshot(
    double? Vo2Max,
    int? ThresholdPaceSecPerKm,
    int? MarathonPredictionSeconds,
    int? HalfMarathonPredictionSeconds,
    int? LactateThresholdHr,
    string? RawJson);

public sealed record WorkoutPushResult(bool Success, string? ExternalRef, string? Error, bool RequiresManualAction = false);

public interface ILlmClient
{
    string ProviderName { get; }

    /// <summary>Returns the model's JSON output (never empty) or throws <see cref="LlmException"/>.</summary>
    Task<string> GenerateJsonAsync(LlmRequest request, CancellationToken ct);
}

/// <param name="JsonSchema">JSON Schema (draft 2020-12 subset) the response must follow.</param>
public sealed record LlmRequest(string Purpose, string SystemPrompt, string UserPrompt, string JsonSchema);

public enum LlmFailure
{
    Timeout,
    Http,
    Empty,
    Schema,
    Integrity,
}

/// <summary>The LLM did not produce a usable answer. There is no rule-based fallback: the operation fails.</summary>
public sealed class LlmException(string purpose, LlmFailure reason, string detail, Exception? inner = null)
    : Exception($"LLM {purpose} failed ({reason}): {detail}", inner)
{
    public string Purpose { get; } = purpose;
    public LlmFailure Reason { get; } = reason;
    public string Detail { get; } = detail;
}

/// <summary>Records every LLM call in llm_calls. Never throws.</summary>
public interface ILlmCallLog
{
    Task RecordAsync(LlmCallRecord record, CancellationToken ct);
}

/// <param name="ContextJson">The context object the service gave the prompt, serialized (not the prompt text).</param>
/// <param name="Response">Raw output of the model; null when nothing came back.</param>
/// <param name="Failure">Null when the answer was used.</param>
public sealed record LlmCallRecord(
    Guid? AthleteId, string Purpose, string Model, string ContextJson, string? Response, LlmFailure? Failure, string? Error, int DurationMs);

public interface IPromptLibrary
{
    string Get(string name);
}

public interface INotifier
{
    /// <returns>Platform message id when the message was delivered, otherwise null.</returns>
    Task<long?> SendAsync(Guid athleteId, OutboundMessage message, CancellationToken ct);
}

public sealed record OutboundMessage(string Text, IReadOnlyList<IReadOnlyList<MessageButton>>? Buttons = null);

public sealed record MessageButton(string Label, string CallbackData);

public interface ITextCatalog
{
    IReadOnlyCollection<string> Languages { get; }
    string Get(string language, string key, params object?[] args);
}
