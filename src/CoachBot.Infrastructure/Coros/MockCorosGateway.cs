using System.Collections.Concurrent;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Services;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure.Coros;

/// <summary>
/// Deterministic synthetic COROS data for development and tests. Clearly labelled "mock" in the database
/// (activities.source / daily_metrics.source) so it can never be mistaken for real data.
/// Scenarios: normal, fatigued (last 3 days: HRV down, RHR up, poor sleep), missing_hrv (no HRV/sleep),
/// sparse_history (only ~10 days of runs, no fitness assessment).
/// </summary>
public sealed class MockCorosGateway(TimeProvider time, IOptions<CorosOptions> options, IOptions<CoachOptions> coach) : ICorosGateway
{
    /// <summary>Runs added at runtime (e.g. the /mock_run dev command) on top of the generated history.</summary>
    public static ConcurrentDictionary<string, ActivitySample> ExtraActivities { get; } = new();

    /// <summary>Workouts "pushed" to the mock calendar, keyed by date + title.</summary>
    public static ConcurrentDictionary<string, PlannedWorkout> PushedWorkouts { get; } = new();

    public string Source => "mock";

    private string Scenario => options.Value.MockScenario;

    private DateOnly Today => AthleteClock.Today(time, coach.Value.TimeZone);

    public Task<CorosCapabilities> GetCapabilitiesAsync(Guid athleteId, CancellationToken ct) =>
        Task.FromResult(new CorosCapabilities(
            new HashSet<string> { "querySportRecords", "queryDailyHealthData", "querySleepHrv", "createScheduledWorkout", "updateScheduledWorkout" },
            CanRead: true, CanPushWorkouts: true, CanUpdateWorkouts: true));

    public Task<IReadOnlyList<ActivitySample>> GetActivitiesAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = AthleteClock.Zone(coach.Value.TimeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        var earliest = Scenario == "sparse_history" ? Today.AddDays(-10) : DateOnly.MinValue;
        var result = new List<ActivitySample>();

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d < earliest || RunKmFor(d) is not double km)
            {
                continue;
            }

            var start = new DateTimeOffset(d.ToDateTime(new TimeOnly(6, 0)), zone.GetUtcOffset(d.ToDateTime(new TimeOnly(6, 0))));
            // Today's run only "exists" once it has been finished.
            if (start.AddHours(1) > nowLocal)
            {
                continue;
            }

            var fatigued = Scenario == "fatigued" && d > Today.AddDays(-3);
            var pace = 360 - (int)(Noise(d, 7) * 20) + (fatigued ? 15 : 0);
            result.Add(new ActivitySample(
                ExternalId: $"mock-{d:yyyyMMdd}",
                SportType: "run",
                StartTime: start.ToUniversalTime(),
                DurationSeconds: (int)(km * pace),
                DistanceMeters: km * 1000,
                AvgPaceSecPerKm: pace,
                AvgHr: 142 + (int)(Noise(d, 3) * 8) + (fatigued ? 12 : 0),
                MaxHr: 165 + (int)(Noise(d, 5) * 10),
                ElevationGainM: 40,
                AvgCadence: 172,
                TrainingLoad: Math.Round(km * 6 * (fatigued ? 1.3 : 1.0)),
                Name: "Mock run",
                RawJson: null));
        }

        result.AddRange(ExtraActivities.Values.Where(a => DateOnly.FromDateTime(a.StartTime.UtcDateTime) >= from.AddDays(-1)));
        return Task.FromResult<IReadOnlyList<ActivitySample>>(result);
    }

    public Task<IReadOnlyList<DailyMetricSample>> GetDailyMetricsAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new List<DailyMetricSample>();
        for (var d = from; d <= to && d <= Today; d = d.AddDays(1))
        {
            var fatigued = Scenario == "fatigued" && d > Today.AddDays(-3);
            var noHrv = Scenario == "missing_hrv";
            result.Add(new DailyMetricSample(
                Date: d,
                RestingHr: 49 + (int)(Noise(d, 11) * 3) + (fatigued ? 9 : 0),
                HrvMs: noHrv ? null : Math.Round(60 + Noise(d, 13) * 6 - (fatigued ? 18 : 0), 1),
                SleepMinutes: noHrv ? null : 420 + (int)(Noise(d, 17) * 50) - (fatigued ? 110 : 0),
                SleepScore: noHrv ? null : 78 - (fatigued ? 25 : 0),
                StressAvg: 28 + (fatigued ? 20 : 0),
                RecoveryPct: fatigued ? 22 : 70 + (int)(Noise(d, 19) * 20),
                LoadRatio: fatigued ? 1.45 : 1.0 + Math.Round(Noise(d, 23) * 0.15, 2)));
        }

        return Task.FromResult<IReadOnlyList<DailyMetricSample>>(result);
    }

    public Task<FitnessSnapshot?> GetFitnessAsync(Guid athleteId, CancellationToken ct) =>
        Task.FromResult(Scenario == "sparse_history"
            ? null
            : new FitnessSnapshot(Vo2Max: 48, ThresholdPaceSecPerKm: 330, MarathonPredictionSeconds: 4 * 3600 + 5 * 60,
                HalfMarathonPredictionSeconds: 1 * 3600 + 58 * 60, LactateThresholdHr: 168, RawJson: null));

    public Task<WorkoutPushResult> PushScheduledWorkoutAsync(Guid athleteId, PlannedWorkout workout, CancellationToken ct)
    {
        var key = workout.CorosRef ?? $"mock-workout-{workout.Id:N}";
        PushedWorkouts[key] = PlanAdjuster.Clone(workout);
        return Task.FromResult(new WorkoutPushResult(true, key, null));
    }

    /// <summary>Typical 4-5 runs/week pattern averaging ~35 km/week.</summary>
    private static double? RunKmFor(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Tuesday => 8,
        DayOfWeek.Wednesday => 6,
        DayOfWeek.Thursday => 8,
        DayOfWeek.Saturday => 5,
        DayOfWeek.Sunday => 14 + d.DayNumber % 3,
        _ => null,
    };

    /// <summary>Deterministic pseudo-noise in [-1, 1] per date and salt.</summary>
    private static double Noise(DateOnly d, int salt) => ((d.DayNumber * 2654435761L + salt * 40503L) % 2001 - 1000) / 1000.0;
}
