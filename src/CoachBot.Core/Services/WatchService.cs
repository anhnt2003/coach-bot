using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CoachBot.Core.Services;

/// <summary>
/// The athlete's state as the watch reports it on a day: the COROS fitness assessment (VO2max, running level, threshold
/// pace, race predictions), the training load (short-term, long-term, ratio, COROS's status) and recovery.
/// </summary>
public sealed record WatchMetrics(
    DateOnly? FitnessDate,
    double? Vo2Max,
    double? RunningLevel,
    int? ThresholdPaceSecPerKm,
    int? FiveKPredictionSeconds,
    int? TenKPredictionSeconds,
    int? HalfMarathonPredictionSeconds,
    int? MarathonPredictionSeconds,
    DateOnly? LoadDate,
    double? ShortTermLoad,
    double? LongTermLoad,
    double? LoadRatio,
    string? LoadStatus,
    int? RecoveryPct)
{
    public bool IsEmpty => FitnessDate is null && LoadDate is null && RecoveryPct is null;

    /// <summary>COROS's prediction for a standard race distance (5K, 10K, half, marathon); null for another distance.</summary>
    public int? PredictionFor(double km) =>
        Math.Abs(km - RaceProfiles.FiveKKm) < 0.15 ? FiveKPredictionSeconds
        : Math.Abs(km - RaceProfiles.TenKKm) < 0.15 ? TenKPredictionSeconds
        : Math.Abs(km - RaceProfiles.HalfKm) < 0.15 ? HalfMarathonPredictionSeconds
        : Math.Abs(km - RaceProfiles.MarathonKm) < 0.15 ? MarathonPredictionSeconds
        : null;
}

public sealed class WatchService(ICoachDb db, ICorosGateway gateway)
{
    /// <summary>Older values than this are not "current".</summary>
    public const int MaxAgeDays = 30;

    /// <summary>The latest values on or before <paramref name="day"/>, each at most <see cref="MaxAgeDays"/> old; null when there are none.</summary>
    public async Task<WatchMetrics?> AtAsync(Guid athleteId, DateOnly day, CancellationToken ct)
    {
        var from = day.AddDays(-MaxAgeDays);
        var rows = db.DailyMetrics.Where(m => m.AthleteId == athleteId && m.Source == gateway.Source && m.Date >= from && m.Date <= day);
        var fitness = await rows
            .Where(m => m.Vo2Max != null || m.RunningLevel != null || m.ThresholdPaceSecPerKm != null || m.HalfMarathonPredictionSeconds != null
                        || m.MarathonPredictionSeconds != null)
            .OrderByDescending(m => m.Date).FirstOrDefaultAsync(ct);
        var load = await rows.Where(m => m.ChronicLoad != null || m.LoadRatio != null).OrderByDescending(m => m.Date).FirstOrDefaultAsync(ct);
        var recovery = await rows.Where(m => m.RecoveryPct != null).OrderByDescending(m => m.Date).Select(m => m.RecoveryPct).FirstOrDefaultAsync(ct);

        var watch = new WatchMetrics(
            fitness?.Date, fitness?.Vo2Max, fitness?.RunningLevel, fitness?.ThresholdPaceSecPerKm,
            fitness?.FiveKPredictionSeconds, fitness?.TenKPredictionSeconds, fitness?.HalfMarathonPredictionSeconds, fitness?.MarathonPredictionSeconds,
            load?.Date, load?.AcuteLoad, load?.ChronicLoad, load?.LoadRatio, load?.LoadStatus, recovery);
        return watch.IsEmpty ? null : watch;
    }
}
