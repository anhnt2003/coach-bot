using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoachBot.Core.Services;

public sealed record SyncResult(bool Success, int NewActivities, IReadOnlyList<Guid> NewRunIds, int MetricDays, string? Error = null);

/// <summary>Pulls activities, daily health metrics and fitness from the gateway and upserts them idempotently.</summary>
public sealed class SyncService(
    ICoachDb db,
    ICorosGateway gateway,
    AlertService alerts,
    MessageFormatter fmt,
    TimeProvider time,
    ILogger<SyncService> logger)
{
    public const int FailureAlertThreshold = 3;

    /// <summary>Regular sync window (days of activities / daily metrics); a full sync backfills enough history to plan from.</summary>
    public const int ActivityDays = 3, MetricDays = 7, FullActivityDays = 56, FullMetricDays = 35;

    public async Task<SyncResult> SyncAsync(Athlete athlete, int activityDays, int metricDays, CancellationToken ct)
    {
        var today = AthleteClock.Today(time, athlete.TimeZone);
        var connection = await db.CorosConnections.FirstOrDefaultAsync(c => c.AthleteId == athlete.Id, ct);

        try
        {
            var activities = await gateway.GetActivitiesAsync(athlete.Id, today.AddDays(-activityDays), today, ct);
            var metrics = await gateway.GetDailyMetricsAsync(athlete.Id, today.AddDays(-metricDays), today, ct);
            var fitness = await gateway.GetFitnessAsync(athlete.Id, ct);

            var newRuns = new List<Guid>();
            var newCount = 0;
            foreach (var sample in activities)
            {
                var (activity, isNew) = await UpsertActivityAsync(athlete.Id, sample, ct);
                if (isNew)
                {
                    newCount++;
                    if (activity.IsRun)
                    {
                        newRuns.Add(activity.Id);
                    }
                }
            }

            foreach (var sample in metrics)
            {
                await UpsertMetricAsync(athlete.Id, sample, ct);
            }

            if (fitness is not null)
            {
                await UpsertMetricAsync(athlete.Id, new DailyMetricSample(today, RawJson: null), ct, fitness);
            }

            if (connection is not null)
            {
                connection.LastSyncAt = time.GetUtcNow();
                connection.LastSyncError = null;
                connection.ConsecutiveSyncFailures = 0;
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Sync done: {Activities} activities ({New} new), {Metrics} metric days", activities.Count, newCount, metrics.Count);
            return new SyncResult(true, newCount, newRuns, metrics.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Sync failed");
            var lang = athlete.Language;
            if (ex is CorosAuthException)
            {
                await alerts.RaiseAsync(athlete.Id, AlertType.CorosAuth, AlertSeverity.Warning,
                    fmt.T(lang, "alert.coros_reconnect"), $"coros_auth:{today}", ct);
            }

            if (connection is not null)
            {
                connection.ConsecutiveSyncFailures++;
                connection.LastSyncError = Truncate(ex.Message, 500);
                await db.SaveChangesAsync(ct);
                if (connection.ConsecutiveSyncFailures == FailureAlertThreshold)
                {
                    await alerts.RaiseAsync(athlete.Id, AlertType.SyncFailure, AlertSeverity.Warning,
                        fmt.T(lang, "alert.sync_failing", connection.ConsecutiveSyncFailures), $"sync_failure:{today}", ct);
                }
            }

            return new SyncResult(false, 0, [], 0, ex.Message);
        }
    }

    private async Task<(Activity Activity, bool IsNew)> UpsertActivityAsync(Guid athleteId, ActivitySample s, CancellationToken ct)
    {
        var existing = db.Activities.Local.FirstOrDefault(a => a.Source == gateway.Source && a.ExternalId == s.ExternalId)
                       ?? await db.Activities.FirstOrDefaultAsync(a => a.Source == gateway.Source && a.ExternalId == s.ExternalId, ct);
        var isNew = existing is null;
        var a = existing ?? new Activity { AthleteId = athleteId, Source = gateway.Source, ExternalId = s.ExternalId, ImportedAt = time.GetUtcNow() };

        a.SportType = s.SportType;
        a.Name = s.Name;
        a.StartTime = s.StartTime;
        a.DurationSeconds = s.DurationSeconds;
        a.DistanceMeters = s.DistanceMeters;
        a.AvgPaceSecPerKm = s.AvgPaceSecPerKm ?? (s.DistanceMeters > 0 ? (int)Math.Round(s.DurationSeconds / (s.DistanceMeters / 1000.0)) : null);
        a.AvgHr = s.AvgHr;
        a.MaxHr = s.MaxHr;
        a.ElevationGainM = s.ElevationGainM;
        a.AvgCadence = s.AvgCadence;
        a.TrainingLoad = s.TrainingLoad;
        a.RawJson = s.RawJson;

        if (isNew)
        {
            db.Activities.Add(a);
        }

        return (a, isNew);
    }

    /// <summary>Merges a sample into the day's row; a missing value never overwrites a known one.</summary>
    private async Task UpsertMetricAsync(Guid athleteId, DailyMetricSample s, CancellationToken ct, FitnessSnapshot? fitness = null)
    {
        var m = db.DailyMetrics.Local.FirstOrDefault(x => x.AthleteId == athleteId && x.Date == s.Date)
                ?? await db.DailyMetrics.FirstOrDefaultAsync(x => x.AthleteId == athleteId && x.Date == s.Date, ct);
        if (m is null)
        {
            m = new DailyMetric { AthleteId = athleteId, Date = s.Date, Source = gateway.Source };
            db.DailyMetrics.Add(m);
        }
        else if (m.Source != gateway.Source)
        {
            // Never blend sources (e.g. mock data left over from development) into one day's metrics.
            db.DailyMetrics.Entry(m).CurrentValues.SetValues(new DailyMetric { AthleteId = athleteId, Date = s.Date, Source = gateway.Source });
        }

        m.RestingHr = s.RestingHr ?? m.RestingHr;
        m.HrvMs = s.HrvMs ?? m.HrvMs;
        m.SleepMinutes = s.SleepMinutes ?? m.SleepMinutes;
        m.SleepScore = s.SleepScore ?? m.SleepScore;
        m.StressAvg = s.StressAvg ?? m.StressAvg;
        m.RecoveryPct = s.RecoveryPct ?? m.RecoveryPct;
        m.AcuteLoad = s.AcuteLoad ?? m.AcuteLoad;
        m.ChronicLoad = s.ChronicLoad ?? m.ChronicLoad;
        m.LoadRatio = s.LoadRatio ?? m.LoadRatio;
        m.RawJson = s.RawJson ?? m.RawJson;
        if (fitness is not null)
        {
            m.Vo2Max = fitness.Vo2Max ?? m.Vo2Max;
            m.ThresholdPaceSecPerKm = fitness.ThresholdPaceSecPerKm ?? m.ThresholdPaceSecPerKm;
            m.MarathonPredictionSeconds = fitness.MarathonPredictionSeconds ?? m.MarathonPredictionSeconds;
        }

        m.UpdatedAt = time.GetUtcNow();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
