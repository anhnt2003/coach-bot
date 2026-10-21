using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Core.Services;

/// <summary>Single-athlete app: the first athlete row is "the" athlete. The schema itself is multi-athlete ready.</summary>
public sealed class AthleteService(ICoachDb db, IOptions<CoachOptions> options, TimeProvider time)
{
    public Task<Athlete?> GetPrimaryAsync(CancellationToken ct) =>
        db.Athletes.OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(ct);

    public async Task<Athlete> GetOrCreatePrimaryAsync(CancellationToken ct)
    {
        var athlete = await GetPrimaryAsync(ct);
        if (athlete is not null)
        {
            return athlete;
        }

        athlete = new Athlete
        {
            TimeZone = options.Value.TimeZone,
            Language = options.Value.Language,
            CreatedAt = time.GetUtcNow(),
        };
        db.Athletes.Add(athlete);
        await db.SaveChangesAsync(ct);
        return athlete;
    }

    public async Task<Athlete> LinkTelegramAsync(long chatId, string? name, CancellationToken ct)
    {
        var athlete = await GetOrCreatePrimaryAsync(ct);
        athlete.TelegramChatId = chatId;
        if (!string.IsNullOrWhiteSpace(name) && athlete.Name == "Runner")
        {
            athlete.Name = name;
        }

        await db.SaveChangesAsync(ct);
        return athlete;
    }

    public Task<RaceGoal?> GetActiveGoalAsync(Guid athleteId, CancellationToken ct) =>
        db.RaceGoals.Where(g => g.AthleteId == athleteId && g.IsActive).OrderByDescending(g => g.CreatedAt).FirstOrDefaultAsync(ct);

    public async Task<RaceGoal> SetGoalAsync(Guid athleteId, DateOnly raceDate, int? goalTimeSeconds, string name, double distanceKm, CancellationToken ct)
    {
        foreach (var existing in await db.RaceGoals.Where(g => g.AthleteId == athleteId && g.IsActive).ToListAsync(ct))
        {
            existing.IsActive = false;
        }

        var goal = new RaceGoal
        {
            AthleteId = athleteId,
            Name = name,
            RaceDate = raceDate,
            GoalTimeSeconds = goalTimeSeconds,
            DistanceKm = distanceKm,
            CreatedAt = time.GetUtcNow(),
        };
        db.RaceGoals.Add(goal);
        await db.SaveChangesAsync(ct);
        return goal;
    }

    public async Task UpdateProfileAsync(Athlete athlete, int? runDays, DayOfWeek? longRunDay, int? maxHr, int? birthYear, string? language, CancellationToken ct)
    {
        if (runDays is int d)
        {
            athlete.RunDaysPerWeek = Math.Clamp(d, 3, 6);
        }

        athlete.LongRunDay = longRunDay ?? athlete.LongRunDay;
        athlete.MaxHr = maxHr ?? athlete.MaxHr;
        athlete.BirthYear = birthYear ?? athlete.BirthYear;
        if (language is "en" or "vi")
        {
            athlete.Language = language;
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class AlertService(ICoachDb db, INotifier notifier, TimeProvider time, ILogger<AlertService> logger)
{
    /// <summary>
    /// Stores an alert unless one with the same dedup key already exists, and sends it unless <paramref name="send"/>
    /// is false (when the alert is already part of another message).
    /// </summary>
    public async Task<bool> RaiseAsync(
        Guid athleteId, AlertType type, AlertSeverity severity, string message, string? dedupKey, CancellationToken ct,
        OutboundMessage? custom = null, bool send = true)
    {
        if (dedupKey is not null && await db.Alerts.AnyAsync(a => a.AthleteId == athleteId && a.DedupKey == dedupKey, ct))
        {
            return false;
        }

        var alert = new Alert
        {
            AthleteId = athleteId,
            Type = type,
            Severity = severity,
            Message = message,
            DedupKey = dedupKey,
            CreatedAt = time.GetUtcNow(),
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Alert {Type}/{Severity} raised ({DedupKey})", type, severity, dedupKey);
        if (!send)
        {
            return true;
        }

        var sent = await notifier.SendAsync(athleteId, custom ?? new OutboundMessage(message), ct);
        if (sent is not null)
        {
            alert.SentAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}
