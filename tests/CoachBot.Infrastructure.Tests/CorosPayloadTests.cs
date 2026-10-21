using System.Text.Json;
using CoachBot.Core.Domain;
using CoachBot.Infrastructure.Coros;

namespace CoachBot.Infrastructure.Tests;

// NOTE: these payloads are SYNTHETIC. They exercise the tolerant parsing helpers, not the real COROS format, which
// remains unverified until `coros discover` output is captured (docs/coros-integration.md).
public class CorosPayloadTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Finds_items_inside_envelopes_and_maps_flexible_field_names()
    {
        var payload = Parse("""
            {"code":"0000","data":{"records":[
              {"labelId":"468","sportType":100,"startTime":1792540800,"distance":10020,"totalTime":3600,"avgHr":150,"avgPace":"5:59","trainingLoad":72},
              {"activityId":"469","sport":"Trail Run","startTime":"2026-10-20T23:00:00Z","distanceKm":12.5,"duration":5400000}
            ]}}
            """);

        var mapped = CorosPayload.Items(payload).Select(CorosMcpGateway.MapActivity).OfType<CoachBot.Core.Abstractions.ActivitySample>().ToList();

        Assert.Equal(2, mapped.Count);
        Assert.Equal("468", mapped[0].ExternalId);
        Assert.Equal("run", mapped[0].SportType);
        Assert.Equal(10020, mapped[0].DistanceMeters, 3);
        Assert.Equal(359, mapped[0].AvgPaceSecPerKm);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792540800), mapped[0].StartTime);
        Assert.Equal("trail_run", mapped[1].SportType);
        Assert.Equal(12500, mapped[1].DistanceMeters, 3);
        Assert.Equal(5400, mapped[1].DurationSeconds); // milliseconds detected
        Assert.NotNull(mapped[0].RawJson);
    }

    [Fact]
    public void Items_without_required_fields_are_skipped_not_guessed()
    {
        var payload = Parse("""{"data":[{"labelId":"1","distance":5000}]}""");
        Assert.Null(CorosMcpGateway.MapActivity(CorosPayload.Items(payload).Single()));
    }

    [Theory]
    [InlineData("""{"date":20261021}""", 2026, 10, 21)]
    [InlineData("""{"happenDay":"20261021"}""", 2026, 10, 21)]
    [InlineData("""{"date":"2026-10-21"}""", 2026, 10, 21)]
    public void Parses_date_formats(string json, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), CorosPayload.Date(Parse(json), "date", "happenDay"));
    }

    [Fact]
    public void Course_is_built_from_schema_and_reports_unmappable_required_fields()
    {
        var schema = Parse("""
            {"type":"object","properties":{"name":{"type":"string"},"sportType":{"type":"integer"},"exercises":{"type":"array"}},
             "required":["name","sportType","exercises"]}
            """);
        var workout = new PlannedWorkout { Title = "Easy run 8 km", Type = WorkoutType.Easy, TargetDistanceKm = 8 };

        var course = CorosMcpGateway.BuildCourse(workout, schema, out var unfilled);

        Assert.Equal("Easy run 8 km", course["name"]);
        Assert.Equal(100, course["sportType"]);
        Assert.Equal(["exercises"], unfilled);
    }

    [Fact]
    public void Tool_args_follow_schema_names_and_date_format()
    {
        var schema = Parse("""
            {"type":"object","properties":{"startDate":{"type":"string","description":"yyyyMMdd"},"endDate":{"type":"string"},"days":{"type":"integer"}},"required":["startDate"]}
            """);

        var args = CorosToolArgs.Build(schema, new Dictionary<string, object?>
        {
            ["startdate"] = new DateOnly(2026, 10, 14),
            ["enddate"] = new DateOnly(2026, 10, 21),
            ["limit"] = 50,
        }, out var missing);

        Assert.Empty(missing);
        Assert.Equal("20261014", args["startDate"]);
        Assert.Equal("2026-10-21", args["endDate"]);
        Assert.False(args.ContainsKey("limit"));
        Assert.False(args.ContainsKey("days"));
    }
}
