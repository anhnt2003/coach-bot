using System.Text.Json;
using System.Text.Json.Serialization;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;

namespace CoachBot.Core.Services;

public static class CoachJson
{
    /// <summary>Used for jsonb columns and LLM payloads: snake_case, enums as strings.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

/// <summary>What a plan was generated from, and the coach's pace table; stored in <see cref="TrainingPlan.InputsJson"/>.</summary>
public sealed record PlanSnapshot(
    PaceTable? Paces,
    double RaceKm,
    double RecentWeeklyKm,
    double LongestRecentRunKm,
    int WeeksOfHistory,
    double? Vo2Max,
    IReadOnlyList<WeeklyFocus>? WeeklyFocus = null,
    AthleteAssessment? Assessment = null)
{
    public static PlanSnapshot? Of(TrainingPlan plan) => CoachJson.Deserialize<PlanSnapshot>(plan.InputsJson);

    /// <summary>The plan's pace table; plans from before the LLM set paces have none, so their sessions are run by effort.</summary>
    public static PaceTable PacesOf(TrainingPlan plan)
    {
        var snapshot = Of(plan);
        return snapshot?.Paces ?? PaceTable.Empty(snapshot?.RaceKm > 0 ? snapshot.RaceKm : RaceProfiles.MarathonKm);
    }
}

public sealed record WeeklyFocus(int Week, string Focus);

public static class AthleteClock
{
    public static TimeZoneInfo Zone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static DateOnly Today(TimeProvider time, string timeZoneId) => LocalDate(time.GetUtcNow(), timeZoneId);

    public static DateOnly LocalDate(DateTimeOffset instant, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone(timeZoneId)).DateTime);
}

/// <summary>Snapshots, restores and copies planned workouts: the audit trail and undo of plan changes.</summary>
public static class PlanAdjuster
{
    private static void MarkChanged(PlannedWorkout w)
    {
        if (w.CorosSyncStatus == SyncStatus.Synced)
        {
            w.CorosDirty = true;
        }
    }

    public static PlannedWorkout Clone(PlannedWorkout w) => new()
    {
        Id = w.Id,
        PlanId = w.PlanId,
        Date = w.Date,
        WeekIndex = w.WeekIndex,
        Phase = w.Phase,
        IsCutbackWeek = w.IsCutbackWeek,
        Type = w.Type,
        TargetDistanceKm = w.TargetDistanceKm,
        TargetDurationMinutes = w.TargetDurationMinutes,
        PaceMinSecPerKm = w.PaceMinSecPerKm,
        PaceMaxSecPerKm = w.PaceMaxSecPerKm,
        HrMin = w.HrMin,
        HrMax = w.HrMax,
        Title = w.Title,
        Description = w.Description,
        StepsJson = w.StepsJson,
        Status = w.Status,
        CompletedActivityId = w.CompletedActivityId,
        CorosSyncStatus = w.CorosSyncStatus,
        CorosRef = w.CorosRef,
        CorosSyncError = w.CorosSyncError,
        CorosSyncedAt = w.CorosSyncedAt,
        CorosDirty = w.CorosDirty,
        UpdatedAt = w.UpdatedAt,
    };

    public static WorkoutState State(PlannedWorkout w) => new(
        w.Date, w.Type, w.TargetDistanceKm, w.TargetDurationMinutes, w.PaceMinSecPerKm, w.PaceMaxSecPerKm, w.HrMin, w.HrMax, w.Title, w.Description,
        w.StepsJson);

    /// <summary>Snapshot used for the before/after audit trail in <see cref="PlanChange"/> and for undo.</summary>
    public static string Snapshot(PlannedWorkout w) => CoachJson.Serialize(State(w));

    public static void Restore(PlannedWorkout w, WorkoutState state)
    {
        w.Date = state.Date;
        w.Type = state.Type;
        w.TargetDistanceKm = state.Km;
        w.TargetDurationMinutes = state.DurationMinutes;
        w.PaceMinSecPerKm = state.PaceMin;
        w.PaceMaxSecPerKm = state.PaceMax;
        w.HrMin = state.HrMin;
        w.HrMax = state.HrMax;
        w.Title = state.Title;
        w.Description = state.Description;
        w.StepsJson = state.Steps;
        MarkChanged(w);
    }
}
