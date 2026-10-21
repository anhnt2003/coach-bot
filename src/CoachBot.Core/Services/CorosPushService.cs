using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Core.Services;

public sealed record PushSummary(int Pushed, int Updated, int Failed, int ManualActions, string? SkippedReason = null);

/// <summary>
/// Rolling push of approved workouts to the COROS calendar. Only the next few days are pushed because COROS tools can
/// neither delete nor reschedule: anything pushed far ahead would go stale as the plan adapts.
/// </summary>
public sealed class CorosPushService(
    ICoachDb db,
    ICorosGateway gateway,
    PlanService plans,
    MessageFormatter fmt,
    INotifier notifier,
    IOptions<CoachOptions> options,
    TimeProvider time,
    ILogger<CorosPushService> logger)
{
    public async Task<PushSummary> PushUpcomingAsync(Athlete athlete, CancellationToken ct)
    {
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        if (plan is null || plan.Status != PlanStatus.Active)
        {
            return new PushSummary(0, 0, 0, 0, plan is null ? "no_active_plan" : "plan_paused");
        }

        var caps = await gateway.GetCapabilitiesAsync(athlete.Id, ct);
        if (!caps.CanPushWorkouts)
        {
            logger.LogInformation("COROS push skipped: gateway does not offer workout scheduling tools");
            return new PushSummary(0, 0, 0, 0, "not_supported");
        }

        var today = AthleteClock.Today(time, athlete.TimeZone);
        var until = today.AddDays(options.Value.PushWindowDays);
        var due = plan.Workouts
            .Where(w => w.Date >= today && w.Date <= until && w.Status == WorkoutStatus.Planned)
            .Where(w => w.CorosSyncStatus is SyncStatus.NotSynced or SyncStatus.Failed || (w.CorosSyncStatus == SyncStatus.Synced && w.CorosDirty))
            .OrderBy(w => w.Date)
            .ToList();

        int pushed = 0, updated = 0, failed = 0;
        var manual = new List<PlannedWorkout>();
        foreach (var w in due)
        {
            var isUpdate = w.CorosSyncStatus == SyncStatus.Synced;
            if (w.Type == WorkoutType.Rest)
            {
                if (isUpdate)
                {
                    // A pushed workout was turned into a rest day; COROS tools cannot delete it.
                    w.CorosSyncStatus = SyncStatus.ManualActionRequired;
                    manual.Add(w);
                }

                continue;
            }

            if (isUpdate && !caps.CanUpdateWorkouts)
            {
                w.CorosSyncStatus = SyncStatus.ManualActionRequired;
                manual.Add(w);
                continue;
            }

            var result = await gateway.PushScheduledWorkoutAsync(athlete.Id, w, ct);
            if (result.Success)
            {
                w.CorosSyncStatus = SyncStatus.Synced;
                w.CorosRef = result.ExternalRef ?? w.CorosRef;
                w.CorosSyncedAt = time.GetUtcNow();
                w.CorosSyncError = null;
                w.CorosDirty = false;
                if (isUpdate)
                {
                    updated++;
                }
                else
                {
                    pushed++;
                }
            }
            else if (result.RequiresManualAction)
            {
                w.CorosSyncStatus = SyncStatus.ManualActionRequired;
                w.CorosSyncError = result.Error;
                manual.Add(w);
            }
            else
            {
                w.CorosSyncStatus = isUpdate ? SyncStatus.Synced : SyncStatus.Failed;
                w.CorosSyncError = result.Error;
                failed++;
            }

            await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        if (manual.Count > 0)
        {
            var lang = athlete.Language;
            await notifier.SendAsync(athlete.Id, new OutboundMessage(
                fmt.T(lang, "push.manual_action") + "\n" + string.Join("\n", manual.Select(w => "• " + fmt.Line(lang, w)))), ct);
        }

        logger.LogInformation("COROS push: {Pushed} new, {Updated} updated, {Failed} failed, {Manual} need manual action", pushed, updated, failed, manual.Count);
        return new PushSummary(pushed, updated, failed, manual.Count);
    }
}
