using System.Text.Json;
using CoachBot.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CoachBot.Core.Services;

/// <summary>
/// Thin structured-output wrapper over <see cref="ILlmClient"/>. Never throws: any provider error, timeout or
/// malformed output yields null, and callers fall back to deterministic templates.
/// </summary>
public sealed class CoachLlm(ILlmClient client, IPromptLibrary prompts, ILogger<CoachLlm> logger)
{
    public bool IsEnabled => client.IsEnabled;

    public string ProviderName => client.ProviderName;

    public async Task<T?> AskAsync<T>(string promptName, object context, string language, string jsonSchema, CancellationToken ct)
        where T : class
    {
        if (!client.IsEnabled)
        {
            return null;
        }

        try
        {
            var system = prompts.Get("system").Replace("{{language}}", language == "vi" ? "Vietnamese" : "English", StringComparison.Ordinal);
            var user = prompts.Get(promptName).Replace("{{context}}", CoachJson.Serialize(context), StringComparison.Ordinal);
            var json = await client.GenerateJsonAsync(new LlmRequest(promptName, system, user, jsonSchema), ct);
            if (json is null)
            {
                return null;
            }

            var result = JsonSerializer.Deserialize<T>(json, CoachJson.Options);
            if (result is null)
            {
                logger.LogWarning("LLM returned empty output for {Prompt}", promptName);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM call for {Prompt} failed; using rule-based fallback", promptName);
            return null;
        }
    }
}

public static class LlmSchemas
{
    public const string PlanRationale = """
        {
          "type": "object",
          "properties": {
            "rationale": { "type": "string", "description": "3-6 sentences explaining the plan structure for the athlete." },
            "weekly_focus": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "week": { "type": "integer" }, "focus": { "type": "string" } },
                "required": ["week", "focus"]
              }
            }
          },
          "required": ["rationale", "weekly_focus"]
        }
        """;

    public const string RunAnalysis = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "2-5 sentences for the athlete about this run and how to approach the next days." },
            "suggestions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "workout_id": { "type": "string" },
                  "action": { "type": "string", "enum": ["reduce_distance", "downgrade", "convert_to_rest", "swap_days", "increase_distance", "upgrade"] },
                  "to_type": { "type": "string", "enum": ["rest", "recovery", "easy", "long", "tempo", "interval", "race_pace"] },
                  "to_km": { "type": "number" },
                  "new_date": { "type": "string", "description": "yyyy-MM-dd, only for swap_days" },
                  "reason": { "type": "string" }
                },
                "required": ["workout_id", "action", "to_type", "to_km", "reason"]
              }
            },
            "symptom_concerns": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["summary", "suggestions", "symptom_concerns"]
        }
        """;

    public const string PlanDesign = """
        {
          "type": "object",
          "properties": {
            "rationale": { "type": "string", "description": "3-6 sentences explaining the plan to the athlete." },
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
                        "title": { "type": "string", "description": "short workout name, e.g. '6 x 1 km at 5K pace' or 'Long run, last 5 km at half-marathon pace'" },
                        "notes": { "type": "string" },
                        "steps": {
                          "type": "array",
                          "description": "the session structure in order (warm-up, main set, cool-down); omit for a plain run at one effort",
                          "items": {
                            "type": "object",
                            "properties": {
                              "repeat": { "type": "integer" },
                              "work": {
                              "type": "object",
                              "properties": {
                                "intensity": { "type": "string", "enum": ["recovery", "easy", "steady", "marathon", "half_marathon", "threshold", "ten_k", "five_k", "repetition", "hill", "strides", "race_pace"] },
                                "km": { "type": "number" },
                                "minutes": { "type": "number" }
                              },
                              "required": ["intensity"]
                            },
                              "rest": {
                              "type": "object",
                              "properties": {
                                "intensity": { "type": "string", "enum": ["recovery", "easy", "steady", "marathon", "half_marathon", "threshold", "ten_k", "five_k", "repetition", "hill", "strides", "race_pace"] },
                                "km": { "type": "number" },
                                "minutes": { "type": "number" }
                              },
                              "required": ["intensity"]
                            }
                            },
                            "required": ["repeat", "work"]
                          }
                        }
                      },
                      "required": ["day", "type", "km"]
                    }
                  }
                },
                "required": ["week", "phase", "cutback", "focus", "sessions"]
              }
            }
          },
          "required": ["rationale", "weeks"]
        }
        """;

    public const string AthleteAssessment = """
        {
          "type": "object",
          "properties": {
            "level": { "type": "string", "enum": ["beginner", "intermediate", "advanced"] },
            "summary": { "type": "string", "description": "2-4 sentences: where the athlete stands today relative to the goal race." },
            "goal_verdict": { "type": "string", "enum": ["realistic", "ambitious", "unrealistic", "no_goal_time"] },
            "goal_comment": { "type": "string", "description": "1-2 sentences on the goal time compared with the predicted time." },
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
          "required": ["level", "summary", "goal_verdict", "goal_comment", "strengths", "limiters", "risks", "priorities"]
        }
        """;

    public const string SymptomTriage = """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "enum": ["none", "illness", "injury", "cardiac"] },
            "is_current": { "type": "boolean", "description": "true only if the athlete says they have/had this symptom recently (not negated, hypothetical, past and resolved, or about someone else)" },
            "evidence": { "type": "string", "description": "the words in the message that support the decision" }
          },
          "required": ["category", "is_current", "evidence"]
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

public sealed record PlanRationaleResponse(string Rationale, IReadOnlyList<WeeklyFocus>? WeeklyFocus);

public sealed record RunAnalysisResponse(string Summary, IReadOnlyList<LlmSuggestion>? Suggestions, IReadOnlyList<string>? SymptomConcerns);

public sealed record LlmSuggestion(string WorkoutId, string Action, string? ToType, double ToKm, string? NewDate, string? Reason);

public sealed record WeeklyReportResponse(string Summary, string? FocusNextWeek);

public sealed record SymptomTriageResponse(string Category, bool IsCurrent, string? Evidence);

/// <summary>The LLM's diagnosis of the athlete against the goal race; stored with the plan and shared with later prompts.</summary>
public sealed record AthleteAssessment(
    string? Level,
    string? Summary,
    string? GoalVerdict,
    string? GoalComment,
    IReadOnlyList<string>? Strengths,
    IReadOnlyList<Limiter>? Limiters,
    IReadOnlyList<string>? Risks,
    IReadOnlyList<PhasePriority>? Priorities);

public sealed record Limiter(string Area, string? Evidence);

public sealed record PhasePriority(string Phase, string Focus);
