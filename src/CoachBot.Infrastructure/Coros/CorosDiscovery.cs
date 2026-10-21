using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure.Coros;

/// <summary>
/// Verifies the live COROS MCP server: records every tool's schema and the raw output of read-only tools so the
/// adapter's mappings are based on real responses. Output contains personal data and is written to a gitignored folder.
/// Write tools are never called here.
/// </summary>
public sealed class CorosDiscovery(CorosMcpSession session, IOptions<CorosOptions> options, TimeProvider time, ILogger<CorosDiscovery> logger)
{
    // Read-only tools, in call order (activities first so detail tools can reuse an activity id).
    private static readonly string[] ReadTools =
    [
        "queryUserInfo", "queryDevices", "querySportRecords", "getActivityDetail", "queryActivityLapData",
        "queryDailyHealthData", "querySleepOverview", "querySleepHrv", "queryAvgHeartRate", "queryRestingHeartRate",
        "queryStressLevel", "queryRecoveryStatus", "queryFitnessAssessmentOverview", "queryTrainingLoadAssessment",
        "queryTrainingSchedule", "queryWorkoutLibrary", "queryTrainingPlanLibrary",
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<string> RunAsync(Guid athleteId, TextWriter output, CancellationToken ct)
    {
        var dir = Path.Combine(options.Value.FixturesDirectory, time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        var tools = await session.ListToolsAsync(athleteId, ct);
        var schemas = tools.ToDictionary(t => t.Name, t => t.JsonSchema);
        await File.WriteAllTextAsync(Path.Combine(dir, "tools.json"), JsonSerializer.Serialize(
            tools.Select(t => new { t.Name, t.Description, inputSchema = t.JsonSchema, outputSchema = t.ReturnJsonSchema }), Indented), ct);
        await output.WriteLineAsync($"tools/list: {tools.Count} tools → {Path.Combine(dir, "tools.json")}");
        await output.WriteLineAsync($"  {string.Join(", ", tools.Select(t => t.Name).Order())}");

        var known = new Dictionary<string, object?>();
        foreach (var name in ReadTools)
        {
            if (!schemas.TryGetValue(name, out var schema))
            {
                await output.WriteLineAsync($"  - {name}: not offered by server");
                continue;
            }

            var args = BuildArgs(schema, known, out var missing);
            if (missing.Count > 0)
            {
                await output.WriteLineAsync($"  - {name}: skipped (no value for required {string.Join(", ", missing)})");
                continue;
            }

            object record;
            try
            {
                var payload = await session.CallAsync(athleteId, name, args, isWrite: false, ct);
                record = new { tool = name, args, ok = true, payload };
                CollectIds(payload, known);
                await output.WriteLineAsync($"  ✓ {name}: {Shape(payload)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record = new { tool = name, args, ok = false, error = ex.Message };
                await output.WriteLineAsync($"  ✗ {name}: {ex.Message}");
                logger.LogWarning(ex, "Discovery call {Tool} failed", name);
            }

            await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.json"), JsonSerializer.Serialize(record, Indented), ct);
        }

        return dir;
    }

    /// <summary>Fills arguments from the tool's JSON schema: dates for the last 7 days, small limits, ids seen earlier.</summary>
    internal Dictionary<string, object?> BuildArgs(JsonElement schema, Dictionary<string, object?> known, out List<string> missing)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var desired = new Dictionary<string, object?>(known)
        {
            ["startdate"] = today.AddDays(-7),
            ["enddate"] = today,
            ["days"] = 7,
            ["limit"] = 5,
        };
        return CorosToolArgs.Build(schema, desired, out missing);
    }

    /// <summary>Remembers the first activity id/sport type seen, for activity-detail tools.</summary>
    private static void CollectIds(JsonElement payload, Dictionary<string, object?> known)
    {
        foreach (var (key, value) in Walk(payload))
        {
            if (key is "labelId" && !known.ContainsKey("labelId"))
            {
                known["labelId"] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
            else if (key is "sportType" && !known.ContainsKey("sportType") && value.TryGetInt32(out var sport))
            {
                known["sportType"] = sport;
            }
        }
    }

    private static IEnumerable<(string Key, JsonElement Value)> Walk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                yield return (p.Name, p.Value);
                foreach (var inner in Walk(p.Value))
                {
                    yield return inner;
                }
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                foreach (var inner in Walk(item))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>Structure only (keys and array sizes), so console output does not echo health values.</summary>
    private static string Shape(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(", ", e.EnumerateObject().Take(12).Select(p => p.Value.ValueKind switch
        {
            JsonValueKind.Array => $"{p.Name}[{p.Value.GetArrayLength()}]",
            JsonValueKind.Object => $"{p.Name}{{…}}",
            _ => p.Name,
        })) + "}",
        JsonValueKind.Array => $"[{e.GetArrayLength()} items]",
        _ => e.ValueKind.ToString(),
    };
}
