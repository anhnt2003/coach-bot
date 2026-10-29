using System.Text.Json.Nodes;
using CoachBot.Core.Abstractions;

namespace CoachBot.IntegrationTests;

/// <summary>
/// An LLM double that answers per prompt with scripted JSON. A null answer behaves like a real failure: the call
/// throws <see cref="LlmException"/>.
/// </summary>
public sealed class ScriptedLlm(Func<LlmRequest, JsonNode, string?> respond) : ILlmClient
{
    public List<LlmRequest> Requests { get; } = [];
    public string ProviderName => "scripted";

    /// <summary>Answers every prompt with <see cref="Default"/>.</summary>
    public ScriptedLlm() : this(Default)
    {
    }

    public Task<string> GenerateJsonAsync(LlmRequest request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var answer = respond(request, Context(request));
        return answer is null
            ? throw new LlmException(request.Purpose, LlmFailure.Empty, "scripted: no answer")
            : Task.FromResult(answer);
    }

    /// <summary>The prompts embed their data as a ```json block.</summary>
    public static JsonNode Context(LlmRequest request)
    {
        var start = request.UserPrompt.IndexOf("```json\n", StringComparison.Ordinal) + "```json\n".Length;
        var end = request.UserPrompt.LastIndexOf("\n```", StringComparison.Ordinal);
        return JsonNode.Parse(request.UserPrompt[start..end])!;
    }

    public const string Assessment = """
        {"level":"intermediate","summary":"Consistent 35 km weeks; threshold work is missing.","predicted_race_seconds":14700,"goal_verdict":"ambitious",
         "goal_comment":"1:50 needs about 3% more speed.","strengths":["consistent easy running"],
         "limiters":[{"area":"threshold","evidence":"no runs faster than easy pace in 8 weeks"},{"area":"long_run_endurance","evidence":"longest run 16 km"}],
         "risks":[],"priorities":[{"phase":"base","focus":"strides and hills"},{"phase":"build","focus":"cruise intervals"}]}
        """;

    /// <summary>The pace table every scripted plan uses.</summary>
    public static JsonArray Paces() =>
    [
        new JsonObject { ["intensity"] = "recovery", ["pace_fast_sec_per_km"] = 400, ["pace_slow_sec_per_km"] = 440 },
        new JsonObject { ["intensity"] = "easy", ["pace_fast_sec_per_km"] = 360, ["pace_slow_sec_per_km"] = 400, ["hr_min"] = 120, ["hr_max"] = 145 },
        new JsonObject { ["intensity"] = "steady", ["pace_fast_sec_per_km"] = 335, ["pace_slow_sec_per_km"] = 350 },
        new JsonObject { ["intensity"] = "race_pace", ["pace_fast_sec_per_km"] = 320, ["pace_slow_sec_per_km"] = 330, ["hr_min"] = 150, ["hr_max"] = 162 },
        new JsonObject { ["intensity"] = "threshold", ["pace_fast_sec_per_km"] = 298, ["pace_slow_sec_per_km"] = 308 },
        new JsonObject { ["intensity"] = "five_k", ["pace_fast_sec_per_km"] = 278, ["pace_slow_sec_per_km"] = 290 },
    ];

    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// <summary>
    /// A plain plan for the context's plan window: easy Tuesday, tempo Thursday, easy Saturday (4+ run days) and the long
    /// run on the athlete's long-run day; the last week ends with the race. <paramref name="session"/> can rewrite sessions.
    /// </summary>
    public static string Design(JsonNode context, Func<JsonObject, JsonObject>? session = null, string rationale = "AI rationale")
    {
        var weeks = context["plan_window"]!["weeks"]!.GetValue<int>();
        var runDays = context["athlete"]!["run_days_per_week"]!.GetValue<int>();
        var longDay = context["athlete"]!["long_run_day"]!.GetValue<string>();
        var raceDay = Array.IndexOf(Days, context["race"]!["weekday"]!.GetValue<string>());
        var raceKm = context["race"]!["distance_km"]!.GetValue<double>();

        var result = new JsonArray();
        for (var n = 1; n <= weeks; n++)
        {
            var byDay = new SortedDictionary<int, JsonObject>
            {
                [1] = new() { ["day"] = "tue", ["type"] = "easy", ["km"] = 8, ["notes"] = "AI: relaxed and conversational" },
                [3] = new() { ["day"] = "thu", ["type"] = "tempo", ["km"] = 8 },
            };
            if (runDays >= 4)
            {
                byDay[5] = new() { ["day"] = "sat", ["type"] = "easy", ["km"] = 6 };
            }

            byDay[Array.IndexOf(Days, longDay)] = new() { ["day"] = longDay, ["type"] = "long", ["km"] = 14 };
            if (n == weeks)
            {
                foreach (var day in byDay.Keys.Where(d => d >= raceDay).ToList())
                {
                    byDay.Remove(day);
                }

                byDay[raceDay] = new() { ["day"] = Days[raceDay], ["type"] = "race", ["km"] = raceKm };
            }

            var phase = n == weeks ? "taper" : n == weeks - 1 ? "peak" : n <= weeks * 0.4 ? "base" : "build";
            result.Add(new JsonObject
            {
                ["week"] = n,
                ["phase"] = phase,
                ["cutback"] = false,
                ["focus"] = "AI focus",
                ["sessions"] = new JsonArray([.. byDay.Values.Select(s => (JsonNode)(session is null ? s : session(s)))]),
            });
        }

        return new JsonObject { ["rationale"] = rationale, ["paces"] = Paces(), ["weeks"] = result }.ToJsonString();
    }

    /// <summary>The planned session of the run's day, if any, as run_analysis lists it.</summary>
    public static string? PlannedSessionOn(JsonNode context, string date) =>
        context["plan"]?["sessions"]?.AsArray()
            .FirstOrDefault(s => s!["date"]!.GetValue<string>() == date && s["status"]!.GetValue<string>() == "planned" && s["type"]!.GetValue<string>() != "rest")
            ?["workout_id"]!.GetValue<string>();

    public static JsonObject Readiness(string level = "green", string summary = "Recovery looks normal.", params string[] concerns) => new()
    {
        ["level"] = level,
        ["confidence"] = "high",
        ["summary"] = summary,
        ["concerns"] = new JsonArray([.. concerns.Select(c => (JsonNode)c)]),
    };

    /// <summary>A sensible answer to every prompt: a plain plan, matched runs, green readiness, no changes, no symptoms.</summary>
    public static string? Default(LlmRequest request, JsonNode context) => request.Purpose switch
    {
        "athlete_assessment" => Assessment,
        "plan_design" => Design(context),
        "run_analysis" => new JsonObject
        {
            ["summary"] = "Good, controlled run.",
            ["matched_workout_id"] = PlannedSessionOn(context, context["run"]!["date"]!.GetValue<string>()) ?? "",
            ["pace_status"] = "on_target",
            ["hr_status"] = "in_zone",
            ["readiness"] = Readiness(),
            ["changes"] = new JsonArray(),
            ["symptom_concerns"] = new JsonArray(),
        }.ToJsonString(),
        "daily_check" => new JsonObject
        {
            ["readiness"] = Readiness(),
            ["advice"] = "Run today's session as planned.",
            ["changes"] = new JsonArray(),
        }.ToJsonString(),
        "symptom_triage" => """{"category":"none","is_current":false,"evidence":""}""",
        "weekly_report" => """{"summary":"Solid week.","focus_next_week":"Keep easy runs easy."}""",
        _ => null,
    };

    /// <summary>A change in the run_analysis / daily_check format.</summary>
    public static JsonObject Change(string workoutId, string date, string type, double km, bool needsApproval, string reason = "fatigue") => new()
    {
        ["workout_id"] = workoutId,
        ["date"] = date,
        ["type"] = type,
        ["km"] = km,
        ["reason"] = reason,
        ["needs_approval"] = needsApproval,
    };
}
