using System.Text.Json;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using Microsoft.Extensions.Logging;

namespace CoachBot.Core.Services;

/// <summary>
/// Structured-output wrapper over <see cref="ILlmClient"/>. Returns the parsed answer once it passed the optional
/// integrity gate, or throws <see cref="LlmException"/>. Every call is recorded in llm_calls.
/// </summary>
public sealed class CoachLlm(ILlmClient client, IPromptLibrary prompts, ILlmCallLog log, TimeProvider time, ILogger<CoachLlm> logger)
{
    public string ProviderName => client.ProviderName;

    /// <param name="gate">Integrity checks on the parsed answer: any message fails the call with <see cref="LlmFailure.Integrity"/>,
    /// so nothing the caller would write from a malformed answer reaches the database.</param>
    public async Task<T> AskAsync<T>(string promptName, Guid? athleteId, object context, string language, string jsonSchema,
        CancellationToken ct, Func<T, IReadOnlyList<string>>? gate = null)
        where T : class
    {
        var contextJson = CoachJson.Serialize(context);
        var system = prompts.Get("system").Replace("{{language}}", language == "vi" ? "Vietnamese" : "English", StringComparison.Ordinal);
        var user = prompts.Get(promptName).Replace("{{context}}", contextJson, StringComparison.Ordinal);
        var started = time.GetTimestamp();
        string? json = null;
        try
        {
            json = await client.GenerateJsonAsync(new LlmRequest(promptName, system, user, jsonSchema), ct);
            T? result;
            try
            {
                result = JsonSerializer.Deserialize<T>(json, CoachJson.Options);
            }
            catch (JsonException ex)
            {
                throw new LlmException(promptName, LlmFailure.Schema, ex.Message, ex);
            }

            if (result is null)
            {
                throw new LlmException(promptName, LlmFailure.Schema, "the answer is the JSON literal null");
            }

            var errors = gate?.Invoke(result) ?? [];
            if (errors.Count > 0)
            {
                throw new LlmException(promptName, LlmFailure.Integrity, string.Join("; ", errors.Take(20)));
            }

            await log.RecordAsync(new LlmCallRecord(athleteId, promptName, client.ProviderName, contextJson, json, null, null, Elapsed(started)), ct);
            return result;
        }
        catch (LlmException ex)
        {
            logger.LogWarning("LLM {Prompt} failed ({Reason}): {Detail}", promptName, ex.Reason, ex.Detail);
            var detail = ex.Detail.Length > 2000 ? ex.Detail[..2000] : ex.Detail;
            await log.RecordAsync(new LlmCallRecord(athleteId, promptName, client.ProviderName, contextJson, json, ex.Reason, detail, Elapsed(started)), ct);
            throw;
        }
    }

    private int Elapsed(long started) => (int)time.GetElapsedTime(started).TotalMilliseconds;
}

public static class LlmSchemas
{
    private const string Intensities = """["recovery", "easy", "steady", "marathon", "half_marathon", "threshold", "ten_k", "five_k", "repetition", "hill", "strides", "race_pace"]""";

    private const string Part = """{ "type": "object", "properties": { "intensity": { "type": "string", "enum": """ + Intensities + """ }, "km": { "type": "number" }, "minutes": { "type": "number" } }, "required": ["intensity"] }""";

    private const string Steps = """
        {
          "type": "array",
          "description": "the session structure in order (warm-up, main set, cool-down); omit for a plain run at one effort",
          "items": {
            "type": "object",
            "properties": { "repeat": { "type": "integer" }, "work": 
        """ + Part + """
        , "rest": 
        """ + Part + """
         },
            "required": ["repeat", "work"]
          }
        }
        """;

    private const string Readiness = """
        {
          "type": "object",
          "properties": {
            "level": { "type": "string", "enum": ["green", "amber", "red"] },
            "confidence": { "type": "string", "enum": ["low", "medium", "high"] },
            "summary": { "type": "string", "description": "1-2 sentences: what the recovery data says about today" },
            "concerns": { "type": "array", "items": { "type": "string" }, "description": "the signals that worry you, each with its value" }
          },
          "required": ["level", "confidence", "summary", "concerns"]
        }
        """;

    private const string Changes = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "workout_id": { "type": "string" },
              "date": { "type": "string", "description": "yyyy-MM-dd: the session's date after the change" },
              "type": { "type": "string", "enum": ["rest", "recovery", "easy", "long", "tempo", "interval", "race_pace"] },
              "km": { "type": "number" },
              "intensity": { "type": "string", "enum": 
        """ + Intensities + """
         },
              "title": { "type": "string" },
              "notes": { "type": "string" },
              "steps": 
        """ + Steps + """
        ,
              "reason": { "type": "string", "description": "one sentence for the athlete" },
              "needs_approval": { "type": "boolean" }
            },
            "required": ["workout_id", "date", "type", "km", "reason", "needs_approval"]
          }
        }
        """;

    public const string RunAnalysis = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "2-5 sentences for the athlete about this run and how to approach the next days." },
            "matched_workout_id": { "type": "string", "description": "workout_id of the planned session this run was, or an empty string" },
            "pace_status": { "type": "string", "enum": ["faster", "on_target", "slower", "unknown"] },
            "hr_status": { "type": "string", "enum": ["above", "in_zone", "below", "unknown"] },
            "readiness": 
        """ + Readiness + """
        ,
            "changes": 
        """ + Changes + """
        ,
            "symptom_concerns": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["summary", "matched_workout_id", "pace_status", "hr_status", "readiness", "changes", "symptom_concerns"]
        }
        """;

    public const string DailyCheck = """
        {
          "type": "object",
          "properties": {
            "readiness": 
        """ + Readiness + """
        ,
            "advice": { "type": "string", "description": "1-3 sentences: how to approach today" },
            "changes": 
        """ + Changes + """
          },
          "required": ["readiness", "advice", "changes"]
        }
        """;

    public const string PlanDesign = """
        {
          "type": "object",
          "properties": {
            "rationale": { "type": "string", "description": "3-6 sentences explaining the plan to the athlete." },
            "paces": {
              "type": "array",
              "description": "your pace (and optional heart-rate) target for each intensity the plan uses",
              "items": {
                "type": "object",
                "properties": {
                  "intensity": { "type": "string", "enum": 
        """ + Intensities + """
         },
                  "pace_fast_sec_per_km": { "type": "integer" },
                  "pace_slow_sec_per_km": { "type": "integer" },
                  "hr_min": { "type": "integer" },
                  "hr_max": { "type": "integer" }
                },
                "required": ["intensity", "pace_fast_sec_per_km", "pace_slow_sec_per_km"]
              }
            },
            "weeks": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "week": { "type": "integer" },
                  "phase": { "type": "string", "enum": ["base", "build", "peak", "taper"] },
                  "cutback": { "type": "boolean" },
                  "focus": { "type": "string" },
                  "sessions": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "day": { "type": "string", "enum": ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] },
                        "type": { "type": "string", "enum": ["recovery", "easy", "long", "tempo", "interval", "race_pace", "race"] },
                        "km": { "type": "number" },
                        "intensity": { "type": "string", "enum": 
        """ + Intensities + """
         },
                        "title": { "type": "string" },
                        "notes": { "type": "string" },
                        "steps": 
        """ + Steps + """
                      },
                      "required": ["day", "type", "km"]
                    }
                  }
                },
                "required": ["week", "phase", "cutback", "focus", "sessions"]
              }
            }
          },
          "required": ["rationale", "paces", "weeks"]
        }
        """;

    public const string AthleteAssessment = """
        {
          "type": "object",
          "properties": {
            "level": { "type": "string", "enum": ["beginner", "intermediate", "advanced"] },
            "summary": { "type": "string", "description": "2-4 sentences: where the athlete stands today relative to the goal race." },
            "predicted_race_seconds": { "type": "integer", "description": "your prediction of the goal race time from current fitness, or 0 if the data is too thin" },
            "goal_verdict": { "type": "string", "enum": ["realistic", "ambitious", "unrealistic", "no_goal_time"] },
            "goal_comment": { "type": "string", "description": "1-2 sentences on the goal time compared with your prediction." },
            "strengths": { "type": "array", "items": { "type": "string" } },
            "limiters": {
              "type": "array",
              "description": "what most limits performance at the goal distance, most important first (at most 3)",
              "items": {
                "type": "object",
                "properties": {
                  "area": { "type": "string", "enum": ["aerobic_base", "long_run_endurance", "threshold", "vo2max", "speed", "race_specific", "consistency", "recovery"] },
                  "evidence": { "type": "string", "description": "the data that shows it" }
                },
                "required": ["area", "evidence"]
              }
            },
            "risks": { "type": "array", "items": { "type": "string" }, "description": "injury/overload risks visible in the data or notes" },
            "priorities": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "phase": { "type": "string", "enum": ["base", "build", "peak", "taper"] },
                  "focus": { "type": "string", "description": "what this phase should develop and with which kinds of sessions" }
                },
                "required": ["phase", "focus"]
              }
            }
          },
          "required": ["level", "summary", "predicted_race_seconds", "goal_verdict", "goal_comment", "strengths", "limiters", "risks", "priorities"]
        }
        """;

    public const string SymptomTriage = """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "enum": ["none", "illness", "injury", "cardiac"] },
            "is_current": { "type": "boolean", "description": "true only if the athlete says they have/had this symptom recently (not negated, hypothetical, past and resolved, or about someone else)" },
            "action": { "type": "string", "enum": ["none", "ask", "pause"] },
            "evidence": { "type": "string", "description": "the words in the message that support the decision" }
          },
          "required": ["category", "is_current", "action", "evidence"]
        }
        """;

    public const string WeeklyReport = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "3-6 sentences reviewing the week." },
            "focus_next_week": { "type": "string" }
          },
          "required": ["summary", "focus_next_week"]
        }
        """;
}

/// <summary>The LLM's readiness verdict as it answers it; checked by <see cref="LlmGates.Readiness"/>.</summary>
public sealed record ReadinessDesign(string? Level, string? Confidence, string? Summary, IReadOnlyList<string>? Concerns);

/// <param name="MatchedWorkoutId">The planned session this run was, or empty.</param>
public sealed record RunAnalysisResponse(
    string? Summary, string? MatchedWorkoutId, string? PaceStatus, string? HrStatus, ReadinessDesign? Readiness,
    IReadOnlyList<ChangeDesign>? Changes, IReadOnlyList<string>? SymptomConcerns);

public sealed record DailyCheckResponse(ReadinessDesign? Readiness, string? Advice, IReadOnlyList<ChangeDesign>? Changes);

public sealed record WeeklyReportResponse(string Summary, string? FocusNextWeek);

/// <param name="Action">none: just a note; ask: ask the athlete to confirm; pause: pause the plan now.</param>
public sealed record SymptomTriageResponse(string? Category, bool IsCurrent, string? Action, string? Evidence);

/// <summary>The LLM's diagnosis of the athlete against the goal race; stored with the plan and shared with later prompts.</summary>
/// <param name="PredictedRaceSeconds">The coach's race-time prediction; 0 or null when the data was too thin.</param>
public sealed record AthleteAssessment(
    string? Level,
    string? Summary,
    string? GoalVerdict,
    string? GoalComment,
    IReadOnlyList<string>? Strengths,
    IReadOnlyList<Limiter>? Limiters,
    IReadOnlyList<string>? Risks,
    IReadOnlyList<PhasePriority>? Priorities,
    int? PredictedRaceSeconds = null);

public sealed record Limiter(string Area, string? Evidence);

public sealed record PhasePriority(string Phase, string Focus);

/// <summary>Integrity checks shared by the prompts that answer a readiness verdict.</summary>
public static class LlmGates
{
    public static readonly string[] PaceStatuses = ["faster", "on_target", "slower", "unknown"];
    public static readonly string[] HrStatuses = ["above", "in_zone", "below", "unknown"];

    /// <summary>Reads the verdict; adds an error for anything that cannot be stored.</summary>
    public static (ReadinessLevel Level, Confidence Confidence) Readiness(ReadinessDesign? r, List<string> errors)
    {
        if (r is null)
        {
            errors.Add("readiness_missing");
            return default;
        }

        if (!Enum.TryParse<ReadinessLevel>(r.Level, ignoreCase: true, out var level) || !Enum.IsDefined(level))
        {
            errors.Add($"readiness_level_invalid: {r.Level ?? "null"}");
        }

        if (!Enum.TryParse<Confidence>(r.Confidence, ignoreCase: true, out var confidence) || !Enum.IsDefined(confidence))
        {
            errors.Add($"readiness_confidence_invalid: {r.Confidence ?? "null"}");
        }

        if (string.IsNullOrWhiteSpace(r.Summary))
        {
            errors.Add("readiness_summary_missing");
        }

        return (level, confidence);
    }
}
