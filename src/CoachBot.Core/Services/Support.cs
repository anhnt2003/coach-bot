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

/// <summary>What a plan was generated from; stored in <see cref="TrainingPlan.InputsJson"/>.</summary>
public sealed record PlanSnapshot(
    TrainingZones Zones,
    PlanBaseline Baseline,
    double RecentWeeklyKm,
    double LongestRecentRunKm,
    int WeeksOfHistory,
    double? Vo2Max,
    IReadOnlyList<WeeklyFocus>? WeeklyFocus = null,
    AthleteAssessment? Assessment = null);

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

/// <summary>Applies <see cref="ProposedChange"/>s to workouts. Pure apart from mutating the given workouts.</summary>
public static class PlanAdjuster
{
    /// <summary>Returns deep copies of <paramref name="workouts"/> with the changes applied, for validation.</summary>
    public static List<PlannedWorkout> Preview(IReadOnlyList<PlannedWorkout> workouts, IReadOnlyList<ProposedChange> changes, TrainingZones zones)
    {
        var copies = workouts.Select(Clone).ToList();
        foreach (var change in changes)
        {
            Apply(copies, change, zones);
        }

        return copies;
    }

    /// <summary>Applies one change in place. Returns the workouts that were modified.</summary>
    public static IReadOnlyList<PlannedWorkout> Apply(IList<PlannedWorkout> workouts, ProposedChange change, TrainingZones zones)
    {
        var target = workouts.FirstOrDefault(w => w.Id == change.WorkoutId);
        if (target is null)
        {
            return [];
        }

        switch (change.Action)
        {
            case ChangeAction.SwapDays when change.NewDate is DateOnly newDate:
                var other = workouts.FirstOrDefault(w => w.Date == newDate && w.Id != target.Id && w.PlanId == target.PlanId);
                if (other is not null)
                {
                    other.Date = target.Date;
                    MarkChanged(other);
                }

                target.Date = newDate;
                MarkChanged(target);
                return other is null ? [target] : [target, other];

            case ChangeAction.ChangePace:
                if (change.NewPaceMinSecPerKm is int newFast && target.PaceMinSecPerKm is int oldFast && oldFast > 0
                    && WorkoutStructure.Parse(target.StepsJson) is { Count: > 0 } blocks)
                {
                    // Every part of a structured session moves with the headline pace.
                    target.StepsJson = WorkoutStructure.Serialize(WorkoutStructure.ScalePaces(blocks, (double)newFast / oldFast));
                }

                target.PaceMinSecPerKm = change.NewPaceMinSecPerKm ?? target.PaceMinSecPerKm;
                target.PaceMaxSecPerKm = change.NewPaceMaxSecPerKm ?? target.PaceMaxSecPerKm;
                MarkChanged(target);
                return [target];

            case ChangeAction.ConvertToRest:
                SetType(target, WorkoutType.Rest, 0, zones);
                return [target];

            default:
                SetType(target, change.ToType, change.ToKm, zones);
                return [target];
        }
    }

    private static void SetType(PlannedWorkout w, WorkoutType type, double km, TrainingZones zones)
    {
        var typeChanged = w.Type != type;
        var kmChanged = Math.Abs(w.TargetDistanceKm - km) > 0.01;
        if (typeChanged)
        {
            // The structure belonged to the old session type; the new type is a plain run.
            w.StepsJson = null;
        }
        else if (kmChanged && WorkoutStructure.Parse(w.StepsJson) is { Count: > 0 } blocks)
        {
            w.StepsJson = WorkoutStructure.Serialize(WorkoutStructure.Resize(blocks, km, zones));
        }

        w.Type = type;
        w.TargetDistanceKm = type == WorkoutType.Rest ? 0 : km;
        if (type == WorkoutType.Rest)
        {
            w.PaceMinSecPerKm = w.PaceMaxSecPerKm = w.HrMin = w.HrMax = w.TargetDurationMinutes = null;
        }
        else if (typeChanged)
        {
            var pace = zones.PaceFor(type);
            var hr = zones.HrFor(type);
            w.PaceMinSecPerKm = pace?.Fast;
            w.PaceMaxSecPerKm = pace?.Slow;
            w.HrMin = hr?.Min;
            w.HrMax = hr?.Max;
        }

        if (type != WorkoutType.Rest && w.PaceMinSecPerKm is int fast && w.PaceMaxSecPerKm is int slow)
        {
            w.TargetDurationMinutes = (int)Math.Round(km * (fast + slow) / 2.0 / 60.0);
        }

        if (typeChanged)
        {
            // Title/description are regenerated by the caller for the new type.
            w.Title = "";
            w.Description = null;
        }

        MarkChanged(w);
    }

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

    /// <summary>Snapshot used for the before/after audit trail in <see cref="PlanChange"/> and for undo.</summary>
    public static string Snapshot(PlannedWorkout w) => CoachJson.Serialize(new WorkoutState(
        w.Date, w.Type, w.TargetDistanceKm, w.TargetDurationMinutes, w.PaceMinSecPerKm, w.PaceMaxSecPerKm, w.HrMin, w.HrMax, w.Title, w.Description,
        w.StepsJson));

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

public sealed record WorkoutState(
    DateOnly Date, WorkoutType Type, double Km, int? DurationMinutes, int? PaceMin, int? PaceMax, int? HrMin, int? HrMax, string Title, string? Description,
    string? Steps = null);
