using System.Text.Json;
using System.Text.RegularExpressions;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.Extensions.Logging;
using static CoachBot.Infrastructure.Coros.CorosPayload;

namespace CoachBot.Infrastructure.Coros;

// Data mapping for the official COROS MCP tools. Argument names/formats come from the live tool schemas; the read
// tools answer with text, parsed by CorosText (formats verified live with `coros discover`).
// The source text is stored with every row.
public sealed partial class CorosMcpGateway
{
    /// <summary>Longest range requested from the day-count tools (a full sync asks for 35 days of metrics).</summary>
    private const int MaxMetricDays = 35;

    private Dictionary<string, JsonElement>? _schemas;

    public async Task<IReadOnlyList<ActivitySample>> GetActivitiesAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new Dictionary<string, ActivitySample>();
        // querySportRecords defaults to 7 days and caps results per call, so query week by week.
        for (var start = from; start <= to; start = start.AddDays(7))
        {
            var end = start.AddDays(6) < to ? start.AddDays(6) : to;
            if (await CallTextAsync(athleteId, "querySportRecords",
                    new() { ["startdate"] = start, ["enddate"] = end, ["limit"] = 50 }, required: true, ct) is not { } text)
            {
                continue;
            }

            var records = CorosText.SportRecords(text);
            foreach (var sample in records)
            {
                result[sample.ExternalId] = sample;
            }

            if (records.Count == 0 && text.Contains("LabelId", StringComparison.Ordinal))
            {
                logger.LogWarning("querySportRecords returned records that could not be read. Run `coros discover`.");
            }
        }

        return [.. result.Values];
    }

    /// <summary>COROS sport codes (from the querySportRecords tool description): 100 run, 101 indoor, 102 trail, 103 track, 104 hike, 2xx bike, 30x swim, 900 walk.</summary>
    internal static string SportName(string? sport) => sport?.ToLowerInvariant() switch
    {
        "100" or "run" or "running" or "outdoor run" => "run",
        "101" or "indoor run" or "treadmill" => "treadmill",
        "102" or "trail run" or "trail running" => "trail_run",
        "103" or "track run" => "track",
        "104" or "hike" => "hike",
        "200" or "201" or "202" or "203" or "204" or "205" or "299" => "bike",
        "300" or "301" => "swim",
        "900" or "walk" => "walk",
        null => "unknown",
        var s when s.Contains("run", StringComparison.Ordinal) => "run",
        var s => s,
    };

    public async Task<IReadOnlyList<DailyMetricSample>> GetDailyMetricsAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var days = Math.Clamp(to.DayNumber - from.DayNumber + 1, 1, MaxMetricDays);
        var byDate = new Dictionary<DateOnly, DailyMetricSample>();

        void Merge(DateOnly date, Func<DailyMetricSample, DailyMetricSample> update)
        {
            if (date >= from && date <= to)
            {
                byDate[date] = update(byDate.TryGetValue(date, out var s) ? s : new DailyMetricSample(date));
            }
        }

        if (await CallTextAsync(athleteId, "queryDailyHealthData", new() { ["days"] = days }, required: true, ct) is { } daily)
        {
            foreach (var (date, d) in CorosText.DailyHealth(daily))
            {
                Merge(date, s => s with { StressAvg = d.StressAvg ?? s.StressAvg, RestingHr = d.RestingHr ?? s.RestingHr, RawJson = d.Raw });
            }
        }

        if (await CallTextAsync(athleteId, "queryRestingHeartRate", new() { ["days"] = days }, required: false, ct) is { } restingHr)
        {
            foreach (var (date, bpm) in CorosText.RestingHeartRate(restingHr))
            {
                Merge(date, s => s with { RestingHr = bpm });
            }
        }

        // Sleep HRV covers at most 7 days per call.
        for (var start = from; start <= to; start = start.AddDays(7))
        {
            var end = start.AddDays(6) < to ? start.AddDays(6) : to;
            if (await CallTextAsync(athleteId, "querySleepHrv", new() { ["startdate"] = start, ["enddate"] = end, ["days"] = 7 }, required: false, ct) is { } hrv)
            {
                foreach (var (date, ms) in CorosText.SleepHrv(hrv))
                {
                    Merge(date, s => s with { HrvMs = ms });
                }
            }
        }

        if (await CallTextAsync(athleteId, "querySleepOverview", new() { ["startdate"] = from, ["enddate"] = to, ["days"] = days }, required: false, ct) is { } sleep)
        {
            foreach (var (date, night) in CorosText.Sleep(sleep))
            {
                Merge(date, s => s with { SleepScore = night.Score ?? s.SleepScore, SleepMinutes = night.Minutes ?? s.SleepMinutes });
            }
        }

        if (await CallTextAsync(athleteId, "queryTrainingLoadAssessment", new() { ["days"] = days }, required: false, ct) is { } load)
        {
            foreach (var (date, l) in CorosText.TrainingLoad(load))
            {
                Merge(date, s => s with { AcuteLoad = l.ShortTerm ?? s.AcuteLoad, ChronicLoad = l.LongTerm ?? s.ChronicLoad, LoadRatio = l.Ratio ?? s.LoadRatio });
            }
        }

        // Current-only value: it belongs to today.
        if (await CallTextAsync(athleteId, "queryRecoveryStatus", new(), required: false, ct) is { } recovery && CorosText.RecoveryPct(recovery) is int pct)
        {
            Merge(to, s => s with { RecoveryPct = pct });
        }

        return [.. byDate.Values.OrderBy(s => s.Date)];
    }

    public async Task<FitnessSnapshot?> GetFitnessAsync(Guid athleteId, CancellationToken ct) =>
        await CallTextAsync(athleteId, "queryFitnessAssessmentOverview", new(), required: false, ct) is { } text ? CorosText.Fitness(text) : null;

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
            return new WorkoutPushResult(false, null, $"COROS course requires unmapped fields: {string.Join(", ", unfilled)}.");
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
            // Unverified (push is off): assumes the "idInPlan: 8" line that queryTrainingSchedule prints.
            var reference = result.ValueKind == JsonValueKind.String && IdInPlan().Match(result.GetString()!) is { Success: true } m ? m.Groups["v"].Value : null;
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

    /// <summary>Calls a read tool and returns its text answer. Structured output would be new: it is logged, not guessed at.</summary>
    private async Task<string?> CallTextAsync(Guid athleteId, string tool, Dictionary<string, object?> desired, bool required, CancellationToken ct)
    {
        if (await CallAsync(athleteId, tool, desired, required, ct) is not { } payload)
        {
            return null;
        }

        if (payload.ValueKind == JsonValueKind.String)
        {
            return payload.GetString();
        }

        logger.LogWarning("COROS {Tool} returned structured output {Shape} instead of text. Run `coros discover`.", tool, Shape(payload));
        return null;
    }

    [GeneratedRegex(@"idInPlan:\s*(?<v>\w+)")]
    private static partial Regex IdInPlan();

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
