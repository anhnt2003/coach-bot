using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Services;
using CoachBot.Infrastructure.Llm;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure.Scheduling;

public sealed class JobsOptions
{
    public const string Section = "Jobs";

    public bool Enabled { get; set; } = true;
    public string SyncCron { get; set; } = "0 */3 * * *";
    public string DailyCheckCron { get; set; } = "30 6 * * *";
    public string WeeklyReportCron { get; set; } = "0 19 * * 0";
    public string PushCron { get; set; } = "0 20 * * *";
    public string ExpiryCron { get; set; } = "15 * * * *";

    /// <summary>On startup, run a job whose last scheduled time passed within this window without a recorded run.</summary>
    public int CatchUpHours { get; set; } = 6;
}

public sealed record CoachJob(string Name, CronExpression Schedule, Func<IServiceProvider, Athlete, CancellationToken, Task> Run);

/// <summary>
/// Runs the coach's recurring jobs on cron schedules in the athlete's time zone. Each occurrence is recorded in
/// job_runs (unique per job + occurrence), so a job never runs twice for the same slot, including across restarts.
/// </summary>
public sealed class CronJobService(
    IServiceScopeFactory scopes,
    IOptions<JobsOptions> options,
    IOptions<CoachOptions> coach,
    TimeProvider time,
    ILogger<CronJobService> logger) : BackgroundService
{
    public IReadOnlyList<CoachJob> Jobs { get; } =
    [
        new("sync", CronExpression.Parse(options.Value.SyncCron), async (sp, athlete, ct) =>
        {
            var result = await sp.GetRequiredService<SyncService>().SyncAsync(athlete, 3, 7, ct);
            if (result.Success && result.NewRunIds.Count > 0)
            {
                await sp.GetRequiredService<AnalysisService>().AnalyzeRunsAsync(athlete, result.NewRunIds, ct);
            }
        }),
        new("daily_check", CronExpression.Parse(options.Value.DailyCheckCron), async (sp, athlete, ct) =>
        {
            var message = await sp.GetRequiredService<ReportService>().DailyCheckAsync(athlete, ct);
            if (message is not null)
            {
                await sp.GetRequiredService<INotifier>().SendAsync(athlete.Id, message, ct);
            }
        }),
        new("weekly_report", CronExpression.Parse(options.Value.WeeklyReportCron), async (sp, athlete, ct) =>
        {
            var message = await sp.GetRequiredService<ReportService>().WeeklyReportAsync(athlete, ct);
            if (message is not null)
            {
                await sp.GetRequiredService<INotifier>().SendAsync(athlete.Id, message, ct);
            }
        }),
        new("coros_push", CronExpression.Parse(options.Value.PushCron), (sp, athlete, ct) =>
            sp.GetRequiredService<CorosPushService>().PushUpcomingAsync(athlete, ct)),
        new("expire_recommendations", CronExpression.Parse(options.Value.ExpiryCron), async (sp, athlete, ct) =>
        {
            var fmt = sp.GetRequiredService<MessageFormatter>();
            var notifier = sp.GetRequiredService<INotifier>();
            foreach (var rec in await sp.GetRequiredService<RecommendationService>().ExpireDueAsync(ct))
            {
                await notifier.SendAsync(rec.AthleteId, new OutboundMessage(fmt.T(athlete.Language, "rec.expired_notice", rec.Summary)), ct);
            }
        }),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Scheduled jobs disabled (Jobs__Enabled=false)");
            return;
        }

        var zone = AthleteClock.Zone(coach.Value.TimeZone);
        logger.LogInformation("Scheduler started in {TimeZone}: {Jobs}", zone.Id,
            string.Join(", ", Jobs.Select(j => $"{j.Name}=[{j.Schedule}]")));

        await CatchUpAsync(zone, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var next = Jobs
                .Select(j => (Job: j, At: j.Schedule.GetNextOccurrence(now, zone)))
                .Where(x => x.At is not null)
                .OrderBy(x => x.At)
                .ToList();
            if (next.Count == 0)
            {
                return;
            }

            var at = next[0].At!.Value;
            await Task.Delay(at - now, time, stoppingToken);
            foreach (var (job, _) in next.Where(x => x.At == at))
            {
                await RunAsync(job, at, stoppingToken);
            }
        }
    }

    private async Task CatchUpAsync(TimeZoneInfo zone, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var job in Jobs)
        {
            var missed = job.Schedule.GetOccurrences(now.AddHours(-options.Value.CatchUpHours), now, zone).LastOrDefault();
            if (missed != default)
            {
                await RunAsync(job, missed, ct);
            }
        }
    }

    public async Task RunAsync(CoachJob job, DateTimeOffset occurrence, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ICoachDb>();
        var athlete = await sp.GetRequiredService<AthleteService>().GetPrimaryAsync(ct);
        if (athlete is null)
        {
            logger.LogDebug("Job {Job} skipped: no athlete yet (send /start)", job.Name);
            return;
        }

        var key = occurrence.ToUniversalTime().ToString("O");
        if (await db.JobRuns.AnyAsync(r => r.JobName == job.Name && r.RunKey == key, ct))
        {
            return;
        }

        var run = new JobRun { JobName = job.Name, RunKey = key, StartedAt = time.GetUtcNow(), Status = "running" };
        db.JobRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return; // Another instance claimed this occurrence.
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["job"] = job.Name, ["athleteId"] = athlete.Id });
        try
        {
            await job.Run(sp, athlete, ct);
            run.Status = "succeeded";
            logger.LogInformation("Job {Job} succeeded", job.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            run.Status = "failed";
            run.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            logger.LogError(ex, "Job {Job} failed", job.Name);
        }

        run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

/// <summary>Non-fatal startup diagnostics: database reachable, LLM model available.</summary>
public sealed class StartupChecks(IServiceScopeFactory scopes, IOptions<LlmOptions> llm, ILogger<StartupChecks> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.CoachDbContext>();
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            logger.LogError("Database is not reachable; check ConnectionStrings__Postgres");
        }

        if (llm.Value.Provider == "gemini" && !string.IsNullOrWhiteSpace(llm.Value.ApiKey))
        {
            await scope.ServiceProvider.GetRequiredService<GeminiLlmClient>().VerifyModelAsync(cancellationToken);
        }
        else if (llm.Value.Provider == "none")
        {
            logger.LogInformation("LLM disabled (Llm__Provider=none): analysis and reports use rule-based templates");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
