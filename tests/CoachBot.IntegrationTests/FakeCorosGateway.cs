using System.Collections.Concurrent;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Services;

namespace CoachBot.IntegrationTests;

/// <summary>
/// Deterministic synthetic COROS data, so the integration tests run offline. Stored with source "fake".
/// Scenarios: normal, fatigued (last 3 days: HRV down, RHR up, poor sleep), missing_hrv (no HRV/sleep),
/// sparse_history (only ~10 days of runs, no fitness assessment).
/// </summary>
public sealed class FakeCorosGateway(TimeProvider time, string scenario, string timeZone) : ICorosGateway
{
    /// <summary>Workouts "pushed" to the fake calendar, keyed by COROS reference.</summary>
    public static ConcurrentDictionary<string, PlannedWorkout> PushedWorkouts { get; } = new();

    public string Source => "fake";

    private DateOnly Today => AthleteClock.Today(time, timeZone);

    public Task<CorosCapabilities> GetCapabilitiesAsync(Guid athleteId, CancellationToken ct) =>
        Task.FromResult(new CorosCapabilities(
            new HashSet<string> { "querySportRecords", "queryDailyHealthData", "querySleepHrv", "createScheduledWorkout", "updateScheduledWorkout" },
            CanRead: true, CanPushWorkouts: true, CanUpdateWorkouts: true));

    public Task<IReadOnlyList<ActivitySample>> GetActivitiesAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = AthleteClock.Zone(timeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        var earliest = scenario == "sparse_history" ? Today.AddDays(-10) : DateOnly.MinValue;
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

            var fatigued = scenario == "fatigued" && d > Today.AddDays(-3);
            var pace = 360 - (int)(Noise(d, 7) * 20) + (fatigued ? 15 : 0);
            result.Add(new ActivitySample(
                ExternalId: $"fake-{d:yyyyMMdd}",
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
                Name: "Fake run",
                RawJson: null));
        }

        return Task.FromResult<IReadOnlyList<ActivitySample>>(result);
    }

    public Task<IReadOnlyList<DailyMetricSample>> GetDailyMetricsAsync(Guid athleteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new List<DailyMetricSample>();
        for (var d = from; d <= to && d <= Today; d = d.AddDays(1))
        {
            var fatigued = scenario == "fatigued" && d > Today.AddDays(-3);
            var noHrv = scenario == "missing_hrv";
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
        Task.FromResult(scenario == "sparse_history"
            ? null
            : new FitnessSnapshot(Vo2Max: 48, ThresholdPaceSecPerKm: 330, MarathonPredictionSeconds: 4 * 3600 + 5 * 60,
                HalfMarathonPredictionSeconds: 1 * 3600 + 58 * 60, LactateThresholdHr: 168, RawJson: null));

    public Task<WorkoutPushResult> PushScheduledWorkoutAsync(Guid athleteId, PlannedWorkout workout, CancellationToken ct)
    {
        var key = workout.CorosRef ?? $"fake-workout-{workout.Id:N}";
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
