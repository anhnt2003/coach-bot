using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Safety;
using Microsoft.EntityFrameworkCore;

namespace CoachBot.Core.Services;

public sealed record ReadinessHistory(ReadinessAssessment Today, IReadOnlyList<ReadinessLevel> Previous);

/// <summary>Loads the data the pure <see cref="ReadinessEvaluator"/> needs and evaluates today and the two days before.</summary>
public sealed class ReadinessService(ICoachDb db, ICorosGateway gateway)
{
    public async Task<ReadinessHistory> EvaluateAsync(Athlete athlete, DateOnly today, CancellationToken ct)
    {
        var from = today.AddDays(-(ReadinessEvaluator.BaselineDays + 8));
        var metrics = await db.DailyMetrics.Where(m => m.AthleteId == athlete.Id && m.Source == gateway.Source && m.Date >= from && m.Date <= today).ToListAsync(ct);

        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var activities = await db.Activities.Where(a => a.AthleteId == athlete.Id && a.Source == gateway.Source && a.StartTime >= since).ToListAsync(ct);
        var runs = activities
            .Where(a => a.IsRun)
            .Select(a => new RunLoadPoint(AthleteClock.LocalDate(a.StartTime, athlete.TimeZone), a.DistanceKm, a.TrainingLoad))
            .ToList();

        var feedbackFrom = today.AddDays(-5);
        var feedback = await db.Feedback.Where(f => f.AthleteId == athlete.Id && f.Date >= feedbackFrom).ToListAsync(ct);
        var activityIds = feedback.Where(f => f.ActivityId != null).Select(f => f.ActivityId).ToList();
        var workoutTypes = await db.PlannedWorkouts
            .Where(w => w.CompletedActivityId != null && activityIds.Contains(w.CompletedActivityId))
            .ToDictionaryAsync(w => w.CompletedActivityId!.Value, w => w.Type, ct);
        var points = feedback
            .Select(f => new FeedbackPoint(f.Date, f.Rpe, f.Soreness,
                f.ActivityId is Guid id && workoutTypes.TryGetValue(id, out var type) ? type : null))
            .ToList();

        ReadinessAssessment At(DateOnly day) => ReadinessEvaluator.Evaluate(new ReadinessInputs(day, metrics, points, runs));
        return new ReadinessHistory(At(today), [At(today.AddDays(-1)).Level, At(today.AddDays(-2)).Level]);
    }
}
