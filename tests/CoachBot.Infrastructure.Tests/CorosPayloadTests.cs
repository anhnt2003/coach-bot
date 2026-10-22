using System.Text.Json;
using CoachBot.Core.Domain;
using CoachBot.Infrastructure.Coros;

namespace CoachBot.Infrastructure.Tests;

public class CorosPayloadTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

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

    [Fact]
    public void Dates_follow_the_schema_format_when_the_name_matches_exactly()
    {
        var schema = Parse("""
            {"type":"object","properties":{"startdate":{"type":"string","description":"yyyyMMdd"}},"required":["startdate"]}
            """);

        var args = CorosToolArgs.Build(schema, new Dictionary<string, object?> { ["startdate"] = new DateOnly(2026, 10, 14) }, out _);

        Assert.Equal("20261014", args["startdate"]);
    }
}

// Formats as returned by the live COROS MCP server (2026-10-22); values and places are made up.
public class CorosTextTests
{
    [Fact]
    public void Sport_records_are_read_from_the_numbered_text_list()
    {
        const string text = """
            Sport Records — 2026-10-15 to 2026-10-22 (2 records)
            ========================

            1. Walk — 2026-10-21
               Location: Park Walk
               Start Coordinates: 10.000000, 100.000000
               Time Window: startTimestamp=1792587965 | endTimestamp=1792592420
               Duration: 1:14:14 | Distance: 2.61 km
               Average Pace: 28:30 /km | Avg HR: 105 bpm | Calories: 231 kcal
               LabelId: 111 | SportType: 900

            2. Outdoor Run — 2026-10-21
               Location: City Run
               Start Coordinates: 10.000000, 100.000000
               Time Window: startTimestamp=1792585116 | endTimestamp=1792587832
               Duration: 45:16 | Distance: 7.02 km
               Average Pace: 6:27 /km | Avg HR: 156 bpm | Calories: 481 kcal
               LabelId: 222 | SportType: 100
            """;

        var records = CorosText.SportRecords(text);

        Assert.Equal(2, records.Count);
        Assert.Equal("walk", records[0].SportType);
        var run = records[1];
        Assert.Equal("222", run.ExternalId);
        Assert.Equal("run", run.SportType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792585116), run.StartTime);
        Assert.Equal(45 * 60 + 16, run.DurationSeconds);
        Assert.Equal(7020, run.DistanceMeters, 3);
        Assert.Equal(6 * 60 + 27, run.AvgPaceSecPerKm);
        Assert.Equal(156, run.AvgHr);
        Assert.Equal("City Run", run.Name);
        Assert.Contains("LabelId: 222", JsonSerializer.Deserialize<string>(run.RawJson!));
        Assert.Equal(74 * 60 + 14, records[0].DurationSeconds);
    }

    [Fact]
    public void Daily_values_are_read_per_day_and_days_without_data_are_skipped()
    {
        var health = CorosText.DailyHealth("""
            Daily Health Data — Last 7 days | Resting HR: 52 bpm | HRV Baseline: 42 ms

            --- 20261016 ---
            Steps: 3,675 | Calories: 285 kcal | Exercise: 6 min
            Stress: Avg 45

            --- 20261017 ---
            Steps: 11,950 | Calories: 1,110 kcal | Exercise: 59 min
            Stress: Avg 77
            """);
        Assert.Equal(45, health[new DateOnly(2026, 10, 16)].StressAvg);
        Assert.Equal(77, health[new DateOnly(2026, 10, 17)].StressAvg);
        Assert.Null(health[new DateOnly(2026, 10, 16)].RestingHr); // the header value is a summary, not that day's

        var restingHr = CorosText.RestingHeartRate("""
            Resting Heart Rate — Last 7 days
            ========================

            2026-10-22: No data
            2026-10-21: 51 bpm
            """);
        Assert.Equal(51, Assert.Single(restingHr).Value);

        var sleep = CorosText.Sleep("""
            2026-10-15
            Sleep Score: 0
            Sleep detail for this day is not available yet.

            2026-10-16
            Sleep Score: 81
            Main Sleep: 7h 12min
            Main Sleep Window: 23:10 - 06:22
            """);
        var night = Assert.Single(sleep);
        Assert.Equal(new DateOnly(2026, 10, 16), night.Key);
        Assert.Equal(81, night.Value.Score);
        Assert.Equal(432, night.Value.Minutes);

        Assert.Empty(CorosText.SleepHrv("""
            Sleep HRV — 2026-10-15 to 2026-10-22
            No data found in the last 8 days.

            Sleep HRV Time Series — Last 8 days
            2026-10-15: no official sleep HRV value for this day; raw HRV points omitted.
            """));
    }

    [Fact]
    public void Training_load_recovery_and_fitness_are_read()
    {
        var load = CorosText.TrainingLoad("""
            Training Load Assessment
            ========================

            2026-10-22
            Comment: Optimized
            Short-Term Load: 91
            Long-Term Load: 73
            Load Ratio: 1.24

            2026-10-21
            Comment: Optimized
            Short-Term Load: 106
            Long-Term Load: 75
            Load Ratio: 1.41
            """);
        Assert.Equal(new CorosText.LoadDay(91, 73, 1.24), load[new DateOnly(2026, 10, 22)]);
        Assert.Equal(1.41, load[new DateOnly(2026, 10, 21)].Ratio);

        Assert.Equal(79, CorosText.RecoveryPct("Recovery Status\n\nRecovery: 79%\nLevel: Moderate training recommended"));

        var fitness = CorosText.Fitness("""
            VO2max: 48
            Running Level: 66
            Threshold Pace: 5:57 /km
            5 km Prediction: 28:45
            Half Marathon Prediction: 2:18:26
            Marathon Prediction: 4:56:53
            """);
        Assert.NotNull(fitness);
        Assert.Equal(48, fitness.Vo2Max);
        Assert.Equal(5 * 60 + 57, fitness.ThresholdPaceSecPerKm);
        Assert.Equal(2 * 3600 + 18 * 60 + 26, fitness.HalfMarathonPredictionSeconds);
        Assert.Equal(4 * 3600 + 56 * 60 + 53, fitness.MarathonPredictionSeconds);
    }

    [Fact]
    public void Optional_filters_declared_required_are_sent_as_null()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"startDate":{"type":"string","description":"Start date in yyyyMMdd format"},
             "minDistanceKm":{"type":"number","description":"Optional minimum distance in kilometers"},
             "labelId":{"type":"string","description":"The activity labelId"}},
             "required":["startDate","minDistanceKm","labelId"]}
            """).RootElement;

        var args = CorosToolArgs.Build(schema, new Dictionary<string, object?> { ["startdate"] = new DateOnly(2026, 10, 14) }, out var missing);

        Assert.True(args.ContainsKey("minDistanceKm"));
        Assert.Null(args["minDistanceKm"]);
        Assert.Equal(["labelId"], missing);
    }
}
