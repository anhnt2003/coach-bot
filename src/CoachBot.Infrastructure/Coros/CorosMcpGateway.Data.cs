using System.Text.Json;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.Extensions.Logging;
using static CoachBot.Infrastructure.Coros.CorosPayload;

namespace CoachBot.Infrastructure.Coros;

// Data mapping for the official COROS MCP tools (names and parameters per coroslab/COROS-MCP docs/en/tools.md).
// Argument names/formats come from the live tool schemas; output field mapping is UNVERIFIED until checked against
// real responses with `coros discover` (see docs/coros-integration.md). Raw payloads are stored with every row.
public sealed partial class CorosMcpGateway
{
    private Dictionary<string, JsonElement>? _schemas;

    public async Task<IReadOnlyList<ActivitySample>> GetActivitiesAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new Dictionary<string, ActivitySample>();
        // querySportRecords defaults to 7 days and caps results per call, so query week by week.
        for (var start = from; start <= to; start = start.AddDays(7))
        {
            var end = start.AddDays(6) < to ? start.AddDays(6) : to;
            var payload = await CallAsync(athleteId, "querySportRecords",
                new Dictionary<string, object?> { ["startdate"] = start, ["enddate"] = end, ["limit"] = 50 }, required: true, ct);
            if (payload is null)
            {
                continue;
            }

            var mapped = 0;
            foreach (var item in Items(payload.Value))
            {
                if (MapActivity(item) is { } sample)
                {
                    result[sample.ExternalId] = sample;
                    mapped++;
                }
            }

            if (mapped == 0 && Items(payload.Value).Any())
            {
                logger.LogWarning("querySportRecords returned items but none could be mapped; payload shape {Shape}. Run `coros discover`.", Shape(payload.Value));
            }
        }

        return [.. result.Values];
    }

    internal static ActivitySample? MapActivity(JsonElement item)
    {
        var id = Text(item, "labelId", "activityId", "id");
        var start = Instant(item, "startTime", "startTimestamp", "beginTime", "startDateTime");
        var distanceKm = Number(item, "distanceKm") ?? (Number(item, "distance", "totalDistance") is double d ? (d > 200 ? d / 1000 : d) : null);
        var duration = Number(item, "durationSeconds", "totalTime", "duration", "workoutTime", "movingTime");
        if (id is null || start is null || distanceKm is null || duration is null)
        {
            return null;
        }

        var seconds = (int)Math.Round(duration.Value > 200_000 ? duration.Value / 1000 : duration.Value);
        var sport = Text(item, "sportType", "sportTypeCode", "sport", "mode");
        return new ActivitySample(
            ExternalId: id,
            SportType: SportName(sport),
            StartTime: start.Value,
            DurationSeconds: seconds,
            DistanceMeters: distanceKm.Value * 1000,
            AvgPaceSecPerKm: PaceSeconds(item, "avgPace", "averagePace", "pace", "avgPaceSecPerKm"),
            AvgHr: (int?)Number(item, "avgHr", "avgHeartRate", "averageHeartRate", "avgHeart"),
            MaxHr: (int?)Number(item, "maxHr", "maxHeartRate", "maxHeart"),
            ElevationGainM: Number(item, "elevGain", "elevationGain", "totalAscent", "ascent"),
            AvgCadence: (int?)Number(item, "avgCadence", "cadence", "avgStepCadence"),
            TrainingLoad: Number(item, "trainingLoad", "load", "tl"),
            Name: Text(item, "name", "title"),
            RawJson: item.GetRawText());
    }

    /// <summary>COROS sport codes: 100 run, 101 indoor run, 102 trail run, 103 track run (per COROS conventions; unverified).</summary>
    internal static string SportName(string? sport) => sport?.ToLowerInvariant() switch
    {
        "100" or "run" or "running" or "outdoor run" => "run",
        "101" or "indoor run" or "treadmill" => "treadmill",
        "102" or "trail run" or "trail running" => "trail_run",
        "103" or "track run" => "track",
        null => "unknown",
        var s when s.Contains("run", StringComparison.Ordinal) => "run",
        var s => s,
    };

    public async Task<IReadOnlyList<DailyMetricSample>> GetDailyMetricsAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var days = Math.Clamp(to.DayNumber - from.DayNumber + 1, 1, 30);
        var byDate = new Dictionary<DateOnly, DailyMetricSample>();

        void Merge(DateOnly date, Func<DailyMetricSample, DailyMetricSample> update) =>
            byDate[date] = update(byDate.TryGetValue(date, out var s) ? s : new DailyMetricSample(date));

        // Daily health summary: resting HR, stress, sleep.
        if (await CallAsync(athleteId, "queryDailyHealthData", new Dictionary<string, object?> { ["days"] = days }, required: true, ct) is { } daily)
        {
            foreach (var item in Items(daily))
            {
                if (Date(item, "date", "day", "happenDay", "dateTime") is DateOnly date && date >= from && date <= to)
                {
                    Merge(date, s => s with
                    {
                        RestingHr = (int?)Number(item, "rhr", "restingHr", "restingHeartRate") ?? s.RestingHr,
                        StressAvg = (int?)Number(item, "avgStress", "stress", "stressAvg") ?? s.StressAvg,
                        SleepMinutes = (int?)Number(item, "sleepMinutes", "totalSleepMinutes", "sleepDuration") ?? s.SleepMinutes,
                        RawJson = item.GetRawText(),
                    });
                }
            }
        }

        // Sleep HRV covers at most 7 days per call.
        var hrvFrom = to.AddDays(-6) > from ? to.AddDays(-6) : from;
        if (await CallAsync(athleteId, "querySleepHrv", new Dictionary<string, object?> { ["startdate"] = hrvFrom, ["enddate"] = to, ["days"] = 7 }, required: false, ct) is { } hrv)
        {
            foreach (var item in Items(hrv))
            {
                if (Date(item, "date", "day", "happenDay") is DateOnly date && Number(item, "hrv", "avgHrv", "sleepHrv", "avgSleepHrv", "value") is double ms)
                {
                    Merge(date, s => s with { HrvMs = ms });
                }
            }
        }

        if (await CallAsync(athleteId, "querySleepOverview", new Dictionary<string, object?> { ["startdate"] = from, ["enddate"] = to, ["days"] = days }, required: false, ct) is { } sleep)
        {
            foreach (var item in Items(sleep))
            {
                if (Date(item, "date", "day", "happenDay") is DateOnly date)
                {
                    Merge(date, s => s with
                    {
                        SleepMinutes = (int?)Number(item, "totalSleepMinutes", "sleepMinutes", "totalMinutes") ?? s.SleepMinutes,
                        SleepScore = (int?)Number(item, "sleepScore", "score") ?? s.SleepScore,
                    });
                }
            }
        }

        // Current-only values belong to today.
        if (await CallAsync(athleteId, "queryRecoveryStatus", new Dictionary<string, object?>(), required: false, ct) is { } recovery
            && Number(recovery, "recoveryPct", "recovery", "recoveryPercent", "recoveryRate", "percentage") is double pct)
        {
            Merge(to, s => s with { RecoveryPct = (int)Math.Round(pct) });
        }

        if (await CallAsync(athleteId, "queryTrainingLoadAssessment", new Dictionary<string, object?> { ["days"] = 7 }, required: false, ct) is { } load)
        {
            Merge(to, s => s with
            {
                LoadRatio = Number(load, "loadRatio", "ratio", "trainingLoadRatio", "acuteChronicRatio") ?? s.LoadRatio,
                AcuteLoad = Number(load, "shortTermLoad", "acuteLoad", "atl") ?? s.AcuteLoad,
                ChronicLoad = Number(load, "longTermLoad", "chronicLoad", "ctl") ?? s.ChronicLoad,
            });
        }

        return [.. byDate.Values.OrderBy(s => s.Date)];
    }

    public async Task<FitnessSnapshot?> GetFitnessAsync(Guid athleteId, CancellationToken ct)
    {
        if (await CallAsync(athleteId, "queryFitnessAssessmentOverview", new Dictionary<string, object?>(), required: false, ct) is not { } payload)
        {
            return null;
        }

        var root = payload;
        int? Prediction(params string[] names) => Number(root, names) is double s && s > 0 ? (int)s : null;
        return new FitnessSnapshot(
            Vo2Max: Number(root, "vo2max", "vo2Max", "VO2max"),
            ThresholdPaceSecPerKm: PaceSeconds(root, "thresholdPace", "ltPace", "lactateThresholdPace"),
            MarathonPredictionSeconds: Prediction("marathonPrediction", "marathonTime", "predictMarathon", "marathon"),
            HalfMarathonPredictionSeconds: Prediction("halfMarathonPrediction", "halfMarathonTime", "predictHalfMarathon", "halfMarathon"),
            LactateThresholdHr: (int?)Number(root, "thresholdHr", "ltHr", "lactateThresholdHr"),
            RawJson: root.GetRawText());
    }

    /// <summary>
    /// Creates (or updates) a standalone workout on the COROS calendar. The `course` object is built from the live
    /// input schema; if it requires fields CoachBot cannot fill, the push fails with a clear message rather than guessing.
    /// Automatic pushing is additionally gated by Coros__EnableWorkoutPush (set it only after `coros test-write` worked).
    /// </summary>
    public async Task<WorkoutPushResult> PushScheduledWorkoutAsync(Guid athleteId, PlannedWorkout workout, CancellationToken ct)
    {
        var isUpdate = workout.CorosRef is not null;
        var tool = isUpdate ? "updateScheduledWorkout" : "createScheduledWorkout";
        var schemas = await SchemasAsync(athleteId, ct);
        if (!schemas.TryGetValue(tool, out var schema))
        {
            return new WorkoutPushResult(false, null, $"{tool} is not offered by the COROS MCP server.", RequiresManualAction: isUpdate);
        }

        var courseSchema = schema.TryGetProperty("properties", out var props) && props.TryGetProperty("course", out var c) ? c : default;
        var course = BuildCourse(workout, courseSchema, out var unfilled);
        if (unfilled.Count > 0)
        {
            return new WorkoutPushResult(false, null, $"COROS course requires unmapped fields: {string.Join(", ", unfilled)}. See docs/coros-integration.md.");
        }

        var desired = new Dictionary<string, object?> { ["date"] = workout.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["course"] = course };
        if (isUpdate)
        {
            desired["idInPlan"] = workout.CorosRef;
        }

        var args = CorosToolArgs.Build(schema, desired, out var missing);
        if (missing.Count > 0)
        {
            return new WorkoutPushResult(false, null, $"{tool} requires unmapped arguments: {string.Join(", ", missing)}.");
        }

        try
        {
            var result = await session.CallAsync(athleteId, tool, args, isWrite: true, ct);
            var reference = Text(result, "idInPlan", "id", "workoutId") ?? Items(result).Select(i => Text(i, "idInPlan", "id")).FirstOrDefault(x => x is not null);
            return new WorkoutPushResult(true, reference ?? workout.CorosRef, null);
        }
        catch (CorosToolException ex)
        {
            logger.LogWarning("COROS {Tool} rejected workout on {Date}: {Message}", tool, workout.Date, ex.Message);
            return new WorkoutPushResult(false, null, ex.Message, RequiresManualAction: isUpdate);
        }
    }

    /// <summary>Fills a COROS course object from the schema's property names. Returns the required fields it could not fill.</summary>
    internal static Dictionary<string, object?> BuildCourse(PlannedWorkout w, JsonElement courseSchema, out List<string> unfilled)
    {
        var known = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = w.Title.Length > 0 ? w.Title : $"{w.Type} {w.TargetDistanceKm} km",
            ["title"] = w.Title,
            ["description"] = w.Description,
            ["overview"] = w.Description,
            ["sportType"] = 100,
            ["distance"] = (int)Math.Round(w.TargetDistanceKm * 1000),
            ["distanceKm"] = w.TargetDistanceKm,
            ["estimatedDistance"] = (int)Math.Round(w.TargetDistanceKm * 1000),
            ["duration"] = w.TargetDurationMinutes * 60,
            ["estimatedTime"] = w.TargetDurationMinutes * 60,
            ["durationMinutes"] = w.TargetDurationMinutes,
        };

        unfilled = [];
        var course = new Dictionary<string, object?>();
        if (courseSchema.ValueKind != JsonValueKind.Object || !courseSchema.TryGetProperty("properties", out var props))
        {
            // No nested schema published: send the common fields and let the server validate.
            foreach (var key in new[] { "name", "description", "sportType", "distance", "duration" })
            {
                course[key] = known[key];
            }

            return course;
        }

        var required = courseSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : [];
        foreach (var p in props.EnumerateObject())
        {
            if (known.TryGetValue(p.Name, out var value) && value is not null)
            {
                course[p.Name] = value;
            }
            else if (required.Contains(p.Name))
            {
                unfilled.Add(p.Name);
            }
        }

        return course;
    }

    private async Task<Dictionary<string, JsonElement>> SchemasAsync(Guid athleteId, CancellationToken ct) =>
        _schemas ??= (await session.ListToolsAsync(athleteId, ct)).ToDictionary(t => t.Name, t => t.JsonSchema);

    /// <summary>Calls a read tool with schema-built arguments. Optional tools that are missing or fail yield null.</summary>
    private async Task<JsonElement?> CallAsync(Guid athleteId, string tool, Dictionary<string, object?> desired, bool required, CancellationToken ct)
    {
        var schemas = await SchemasAsync(athleteId, ct);
        if (!schemas.TryGetValue(tool, out var schema))
        {
            if (required)
            {
                throw new CorosToolException(tool, "not offered by the COROS MCP server");
            }

            return null;
        }

        var args = CorosToolArgs.Build(schema, desired, out var missing);
        if (missing.Count > 0)
        {
            logger.LogWarning("Skipping {Tool}: no value for required arguments {Missing}", tool, string.Join(", ", missing));
            return null;
        }

        try
        {
            return await session.CallAsync(athleteId, tool, args, isWrite: false, ct);
        }
        catch (CorosToolException ex) when (!required)
        {
            logger.LogWarning("Optional COROS tool {Tool} failed: {Message}", tool, ex.Message);
            return null;
        }
    }
}
