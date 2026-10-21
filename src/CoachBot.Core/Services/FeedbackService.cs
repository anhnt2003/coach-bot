using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoachBot.Core.Services;

/// <summary>
/// Records athlete feedback and screens every free-text message for red-flag symptoms: a deterministic keyword
/// detector plus (when enabled) an LLM that understands negation and context.
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
        return new OutboundMessage(fmt.T(lang, rpe >= 8 ? "feedback.rpe_high" : "feedback.rpe_saved", feedback.Rpe));
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
        return new OutboundMessage(fmt.T(athlete.Language, soreness >= 7 ? "feedback.soreness_high" : "feedback.saved"));
    }

    /// <summary>
    /// Stores free text (notes, /feedback, /symptom) and screens it for red flags:
    /// <list type="bullet">
    /// <item>keyword hit, LLM unavailable or agreeing → pause immediately;</item>
    /// <item>keyword hit, LLM says negated/not current ("no chest pain") → ask the athlete to confirm, no pause yet;</item>
    /// <item>no keyword, LLM finds a current symptom → pause immediately.</item>
    /// </list>
    /// The LLM can never silently clear a keyword hit: the athlete always decides.
    /// </summary>
    public async Task<OutboundMessage> RecordNoteAsync(Athlete athlete, string text, bool explicitSymptomReport, CancellationToken ct)
    {
        var lang = athlete.Language;
        var keyword = SymptomDetector.Detect(text);
        var triage = await llm.AskAsync<SymptomTriageResponse>("symptom_triage", new
        {
            message = text,
            keyword_matches = keyword.Matched,
        }, lang, LlmSchemas.SymptomTriage, ct);
        var llmCategory = triage is { IsCurrent: true } && Enum.TryParse<SymptomCategory>(triage.Category, ignoreCase: true, out var c) ? c : SymptomCategory.None;

        var category = keyword.Category > llmCategory ? keyword.Category : llmCategory;
        var needsConfirmation = keyword.IsRedFlag && triage is not null && llmCategory == SymptomCategory.None;

        db.Feedback.Add(new AthleteFeedback
        {
            AthleteId = athlete.Id,
            Date = AthleteClock.Today(time, athlete.TimeZone),
            Notes = text.Length > 2000 ? text[..2000] : text,
            Symptom = needsConfirmation ? SymptomCategory.None : category,
            RedFlag = !needsConfirmation && category != SymptomCategory.None,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);

        if (needsConfirmation)
        {
            logger.LogInformation("Keyword red flag ({Category}) but LLM judged it not current; asking the athlete to confirm", keyword.Category);
            var cat = keyword.Category.ToString().ToLowerInvariant();
            return new OutboundMessage(fmt.T(lang, "safety.confirm_symptom", string.Join(", ", keyword.Matched)),
            [
                [new MessageButton(fmt.T(lang, "symptom.button.yes_current"), $"sym:{cat}")],
                [new MessageButton(fmt.T(lang, "symptom.button.not_current"), "sym:none")],
            ]);
        }

        if (category == SymptomCategory.None)
        {
            return new OutboundMessage(fmt.T(lang, explicitSymptomReport ? "safety.no_red_flag" : "feedback.note_saved"));
        }

        var matched = keyword.IsRedFlag ? keyword.Matched : [$"llm: {triage?.Evidence}"];
        return await HandleRedFlagAsync(athlete, new SymptomAssessment(category, matched), ct);
    }

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
        return await HandleRedFlagAsync(athlete, new SymptomAssessment(category, []), ct);
    }

    private async Task<OutboundMessage> HandleRedFlagAsync(Athlete athlete, SymptomAssessment assessment, CancellationToken ct)
    {
        var lang = athlete.Language;
        var category = assessment.Category.ToString().ToLowerInvariant();
        var paused = await PauseAsync(athlete, $"symptom:{category}", ct);
        var message = fmt.T(lang, $"safety.{category}") + "\n\n" + fmt.T(lang, paused ? "safety.plan_paused" : "safety.no_active_plan");

        await alerts.RaiseAsync(athlete.Id, AlertType.Symptom, assessment.Category == SymptomCategory.Cardiac ? AlertSeverity.Critical : AlertSeverity.Warning,
            $"{category}: {string.Join(", ", assessment.Matched)}", null, ct, send: false);
        logger.LogWarning("Red-flag symptom reported ({Category}); plan paused={Paused}", category, paused);
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
