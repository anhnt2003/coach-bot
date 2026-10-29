using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoachBot.Core.Services;

/// <summary>
/// Records athlete feedback. Every free-text message is read by the LLM, which decides whether it reports a symptom that
/// should pause the plan. The /symptom buttons pause without the LLM: that is the athlete's own decision.
/// </summary>
public sealed class FeedbackService(
    ICoachDb db,
    PlanService plans,
    AlertService alerts,
    CoachLlm llm,
    MessageFormatter fmt,
    TimeProvider time,
    ILogger<FeedbackService> logger)
{
    public async Task<OutboundMessage> RecordRpeAsync(Athlete athlete, Guid activityId, int rpe, CancellationToken ct)
    {
        var lang = athlete.Language;
        var activity = await db.Activities.FirstOrDefaultAsync(a => a.Id == activityId && a.AthleteId == athlete.Id, ct);
        if (activity is null)
        {
            return new OutboundMessage(fmt.T(lang, "feedback.unknown_activity"));
        }

        var existing = await db.Feedback.FirstOrDefaultAsync(f => f.ActivityId == activityId && f.Rpe != null, ct);
        var feedback = existing ?? new AthleteFeedback
        {
            AthleteId = athlete.Id,
            ActivityId = activityId,
            Date = AthleteClock.LocalDate(activity.StartTime, athlete.TimeZone),
            CreatedAt = time.GetUtcNow(),
        };
        feedback.Rpe = Math.Clamp(rpe, 1, 10);
        if (existing is null)
        {
            db.Feedback.Add(feedback);
        }

        await db.SaveChangesAsync(ct);
        return new OutboundMessage(fmt.T(lang, "feedback.rpe_saved", feedback.Rpe));
    }

    public async Task<OutboundMessage> RecordSorenessAsync(Athlete athlete, int soreness, CancellationToken ct)
    {
        db.Feedback.Add(new AthleteFeedback
        {
            AthleteId = athlete.Id,
            Date = AthleteClock.Today(time, athlete.TimeZone),
            Soreness = Math.Clamp(soreness, 0, 10),
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return new OutboundMessage(fmt.T(athlete.Language, "feedback.saved"));
    }

    /// <summary>Stores a free-text note before anyone reads it, so a failed triage loses nothing.</summary>
    public async Task<AthleteFeedback> SaveNoteAsync(Athlete athlete, string text, CancellationToken ct)
    {
        var note = new AthleteFeedback
        {
            AthleteId = athlete.Id,
            Date = AthleteClock.Today(time, athlete.TimeZone),
            Notes = text.Length > 2000 ? text[..2000] : text,
            CreatedAt = time.GetUtcNow(),
        };
        db.Feedback.Add(note);
        await db.SaveChangesAsync(ct);
        return note;
    }

    /// <summary>
    /// The LLM reads a saved note and decides: pause the plan for a current red-flag symptom, ask the athlete when it is
    /// unsure, or nothing. Throws <see cref="LlmException"/> when the answer is unusable; the note stays saved.
    /// </summary>
    /// <param name="explicitSymptomReport">From /symptom: the athlete expects an answer even when it is nothing serious.</param>
    /// <returns>The reply, or null when a plain note needs none.</returns>
    public async Task<OutboundMessage?> TriageAsync(Athlete athlete, Guid feedbackId, bool explicitSymptomReport, CancellationToken ct)
    {
        var lang = athlete.Language;
        var note = await db.Feedback.FirstOrDefaultAsync(f => f.Id == feedbackId && f.AthleteId == athlete.Id, ct);
        if (note?.Notes is null)
        {
            return null;
        }

        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        var since = note.CreatedAt.AddDays(-14);
        var recent = await db.Feedback
            .Where(f => f.AthleteId == athlete.Id && f.Id != note.Id && f.Notes != null && f.CreatedAt >= since)
            .OrderByDescending(f => f.CreatedAt).Take(5)
            .Select(f => new { f.Date, text = f.Notes })
            .ToListAsync(ct);

        var triage = await llm.AskAsync<SymptomTriageResponse>("symptom_triage", athlete.Id, new
        {
            message = note.Notes,
            plan_status = plan is null ? "none" : plan.Status == PlanStatus.Paused ? "paused" : "active",
            recent_notes = recent,
        }, lang, LlmSchemas.SymptomTriage, ct, TriageErrors);

        var category = Category(triage.Category);
        var evidence = triage.Evidence?.Trim() ?? "";
        switch (triage.Action)
        {
            case "pause":
                note.Symptom = category;
                note.RedFlag = true;
                await db.SaveChangesAsync(ct);
                return await HandleRedFlagAsync(athlete, category, evidence, ct);
            case "ask":
                var cat = category.ToString().ToLowerInvariant();
                return new OutboundMessage(fmt.T(lang, "safety.confirm_symptom", evidence),
                [
                    [new MessageButton(fmt.T(lang, "symptom.button.yes_current"), $"sym:{cat}")],
                    [new MessageButton(fmt.T(lang, "symptom.button.not_current"), "sym:none")],
                ]);
            default:
                return explicitSymptomReport ? new OutboundMessage(fmt.T(lang, "safety.no_red_flag")) : null;
        }
    }

    private static readonly string[] TriageActions = ["none", "ask", "pause"];

    private static IReadOnlyList<string> TriageErrors(SymptomTriageResponse t)
    {
        var errors = new List<string>();
        if (!Enum.TryParse<SymptomCategory>(t.Category, ignoreCase: true, out var category) || !Enum.IsDefined(category))
        {
            errors.Add($"category_invalid: {t.Category ?? "null"}");
        }
        else if (t.Action is "pause" or "ask" && category == SymptomCategory.None)
        {
            errors.Add($"action_without_category: {t.Action} needs a symptom category");
        }

        if (!TriageActions.Contains(t.Action))
        {
            errors.Add($"action_invalid: {t.Action ?? "null"}");
        }

        return errors;
    }

    private static SymptomCategory Category(string? value) =>
        Enum.TryParse<SymptomCategory>(value, ignoreCase: true, out var category) ? category : SymptomCategory.None;

    /// <summary>Quick-report buttons from /symptom.</summary>
    public Task<OutboundMessage> ReportCategoryAsync(Athlete athlete, SymptomCategory category, CancellationToken ct) =>
        category == SymptomCategory.None
            ? Task.FromResult(new OutboundMessage(fmt.T(athlete.Language, "safety.no_red_flag")))
            : RecordCategoryAsync(athlete, category, ct);

    private async Task<OutboundMessage> RecordCategoryAsync(Athlete athlete, SymptomCategory category, CancellationToken ct)
    {
        db.Feedback.Add(new AthleteFeedback
        {
            AthleteId = athlete.Id,
            Date = AthleteClock.Today(time, athlete.TimeZone),
            Symptom = category,
            RedFlag = true,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return await HandleRedFlagAsync(athlete, category, null, ct);
    }

    private async Task<OutboundMessage> HandleRedFlagAsync(Athlete athlete, SymptomCategory category, string? evidence, CancellationToken ct)
    {
        var lang = athlete.Language;
        var cat = category.ToString().ToLowerInvariant();
        var paused = await PauseAsync(athlete, $"symptom:{cat}", ct);
        var message = fmt.T(lang, $"safety.{cat}") + "\n\n" + fmt.T(lang, paused ? "safety.plan_paused" : "safety.no_active_plan");

        await alerts.RaiseAsync(athlete.Id, AlertType.Symptom, category == SymptomCategory.Cardiac ? AlertSeverity.Critical : AlertSeverity.Warning,
            string.IsNullOrWhiteSpace(evidence) ? cat : $"{cat}: {evidence}", null, ct, send: false);
        logger.LogWarning("Red-flag symptom reported ({Category}); plan paused={Paused}", cat, paused);
        return new OutboundMessage(message);
    }

    /// <summary>Safety hold: reduces load to zero, so it needs no approval. COROS pushes stop while paused.</summary>
    public async Task<bool> PauseAsync(Athlete athlete, string reason, CancellationToken ct)
    {
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        if (plan is null)
        {
            return false;
        }

        plan.Status = PlanStatus.Paused;
        plan.PauseReason = reason;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<OutboundMessage> RequestResumeAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        if (plan?.Status != PlanStatus.Paused)
        {
            return new OutboundMessage(fmt.T(lang, "resume.not_paused"));
        }

        var key = plan.PauseReason?.StartsWith("symptom:", StringComparison.Ordinal) == true ? "resume.confirm_symptom_free" : "resume.confirm";
        return new OutboundMessage(fmt.T(lang, key),
            [[new MessageButton(fmt.T(lang, "button.resume_confirm"), "resume:confirm"), new MessageButton(fmt.T(lang, "button.cancel"), "resume:cancel")]]);
    }

    public async Task<OutboundMessage> ConfirmResumeAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var plan = await plans.GetCurrentPlanAsync(athlete.Id, ct);
        if (plan?.Status != PlanStatus.Paused)
        {
            return new OutboundMessage(fmt.T(lang, "resume.not_paused"));
        }

        plan.Status = PlanStatus.Active;
        plan.PauseReason = null;
        await db.SaveChangesAsync(ct);
        return new OutboundMessage(fmt.T(lang, "resume.done"));
    }
}
