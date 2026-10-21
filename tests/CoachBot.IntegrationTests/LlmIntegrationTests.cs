using System.Text.Json;
using System.Text.Json.Nodes;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Safety;
using CoachBot.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CoachBot.IntegrationTests;

/// <summary>An LLM double that answers per prompt with scripted JSON, so the guardrails can be tested deterministically.</summary>
public sealed class ScriptedLlm(Func<LlmRequest, JsonNode, string?> respond) : ILlmClient
{
    public List<LlmRequest> Requests { get; } = [];
    public bool IsEnabled => true;
    public string ProviderName => "scripted";

    public Task<string?> GenerateJsonAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request, Context(request)));
    }

    /// <summary>The prompts embed their data as a ```json block.</summary>
    public static JsonNode Context(LlmRequest request)
    {
        var start = request.UserPrompt.IndexOf("```json\n", StringComparison.Ordinal) + "```json\n".Length;
        var end = request.UserPrompt.LastIndexOf("\n```", StringComparison.Ordinal);
        return JsonNode.Parse(request.UserPrompt[start..end])!;
    }

    /// <summary>Echoes the rule-based baseline as the "AI design", optionally transformed.</summary>
    public static string Design(JsonNode context, Func<JsonObject, JsonObject>? session = null, string rationale = "AI rationale")
    {
        var weeks = new JsonArray();
        foreach (var week in context["baseline_plan"]!.AsArray())
        {
            var sessions = new JsonArray();
            foreach (var s in week!["sessions"]!.AsArray())
            {
                var copy = s!.DeepClone().AsObject();
                if (copy["type"]!.GetValue<string>() == "easy")
                {
                    copy["notes"] = "AI: relaxed and conversational";
                }

                sessions.Add(session is null ? copy : session(copy));
            }

            weeks.Add(new JsonObject
            {
                ["week"] = week["week"]!.GetValue<int>(),
                ["phase"] = week["phase"]!.GetValue<string>(),
                ["cutback"] = week["cutback"]!.GetValue<bool>(),
                ["focus"] = "AI focus",
                ["sessions"] = sessions,
            });
        }

        return new JsonObject { ["rationale"] = rationale, ["weeks"] = weeks }.ToJsonString();
    }
}

[Collection(PostgresCollection.Name)]
public class LlmIntegrationTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private static JsonObject Doubled(JsonObject s)
    {
        s["km"] = s["km"]!.GetValue<double>() * 2;
        return s;
    }

    private async Task<(ServiceProvider Sp, AsyncServiceScope Scope, Athlete Athlete)> SetupAsync(
        ScriptedLlm llm, string scenario = "normal", RaceGoal? goal = null)
    {
        var sp = TestHost.Build(await pg.CreateDatabaseAsync(), new FakeTimeProvider(Start), new CapturingNotifier(), scenario, llm);
        var scope = sp.CreateAsyncScope();
        var athletes = scope.ServiceProvider.GetRequiredService<AthleteService>();
        var athlete = await athletes.GetOrCreatePrimaryAsync(default);
        goal ??= new RaceGoal { RaceDate = new DateOnly(2027, 3, 7), GoalTimeSeconds = 4 * 3600, Name = "Hanoi Marathon", DistanceKm = 42.195 };
        await athletes.SetGoalAsync(athlete.Id, goal.RaceDate, goal.GoalTimeSeconds, goal.Name, goal.DistanceKm, default);
        await scope.ServiceProvider.GetRequiredService<SyncService>().SyncAsync(athlete, 56, 35, default);
        return (sp, scope, athlete);
    }

    [Fact]
    public async Task Llm_designed_plan_is_used_when_it_passes_every_safety_rule()
    {
        var llm = new ScriptedLlm((r, ctx) => r.Purpose == "plan_design" ? ScriptedLlm.Design(ctx) : null);
        var (sp, scope, athlete) = await SetupAsync(llm);
        await using var _ = sp;
        await using var __ = scope;

        var result = await scope.ServiceProvider.GetRequiredService<PlanService>().GenerateAsync(athlete, default);

        var plan = result.Plan!;
        Assert.StartsWith("llm:", plan.GeneratedBy, StringComparison.Ordinal);
        Assert.Equal("AI rationale", plan.Rationale);
        Assert.Contains(plan.Workouts, w => w.Description!.StartsWith("AI: relaxed", StringComparison.Ordinal));
        Assert.DoesNotContain("plan.llm_rejected", result.Warnings);
        var context = ScriptedLlm.Context(llm.Requests.Single(r => r.Purpose == "plan_design"));
        Assert.NotNull(context["rules"]);
        Assert.Equal(athlete.RunDaysPerWeek, context["athlete"]!["run_days_per_week"]!.GetValue<int>());
    }

    private const string Assessment = """
        {"level":"intermediate","summary":"Consistent 35 km weeks; threshold work is missing.","goal_verdict":"ambitious",
         "goal_comment":"1:50 needs about 3% more speed.","strengths":["consistent easy running"],
         "limiters":[{"area":"threshold","evidence":"no runs faster than easy pace in 8 weeks"},{"area":"long_run_endurance","evidence":"longest run 16 km"}],
         "risks":[],"priorities":[{"phase":"base","focus":"strides and hills"},{"phase":"build","focus":"cruise intervals"}]}
        """;

    /// <summary>Gives every quality session a structure that keeps its distance: warm-up, 2 repeats, cool-down.</summary>
    private static JsonObject Structured(JsonObject s)
    {
        var (title, intensity, repKm) = s["type"]!.GetValue<string>() switch
        {
            "tempo" => ("2 × 1.5 km at threshold", "threshold", 1.5),
            "interval" => ("2 × 1.5 km at 5K pace", "five_k", 1.5),
            "race_pace" => ("2 × 1.5 km at half-marathon pace", "race_pace", 1.5),
            _ => (null, null, 0.0),
        };
        if (title is null)
        {
            return s;
        }

        var km = s["km"]!.GetValue<double>();
        var easy = (km - 2 * (repKm + 0.5)) / 2;
        s["title"] = title;
        s["steps"] = new JsonArray(
            new JsonObject { ["repeat"] = 1, ["work"] = new JsonObject { ["intensity"] = "easy", ["km"] = easy } },
            new JsonObject
            {
                ["repeat"] = 2,
                ["work"] = new JsonObject { ["intensity"] = intensity, ["km"] = repKm },
                ["rest"] = new JsonObject { ["intensity"] = "recovery", ["km"] = 0.5 },
            },
            new JsonObject { ["repeat"] = 1, ["work"] = new JsonObject { ["intensity"] = "easy", ["km"] = easy } });
        return s;
    }

    [Fact]
    public async Task Half_marathon_goal_gets_a_diagnosis_and_structured_workouts()
    {
        var llm = new ScriptedLlm((r, ctx) => r.Purpose switch
        {
            "athlete_assessment" => Assessment,
            "plan_design" => ScriptedLlm.Design(ctx, Structured),
            _ => null,
        });
        var goal = new RaceGoal { RaceDate = new DateOnly(2027, 1, 24), GoalTimeSeconds = 6600, Name = "VnExpress Half", DistanceKm = 21.0975 };
        var (sp, scope, athlete) = await SetupAsync(llm, goal: goal);
        await using var _ = sp;
        await using var __ = scope;
        var plans = scope.ServiceProvider.GetRequiredService<PlanService>();

        var result = await plans.GenerateAsync(athlete, default);

        var plan = result.Plan!;
        Assert.StartsWith("llm:", plan.GeneratedBy, StringComparison.Ordinal);
        var snapshot = CoachJson.Deserialize<PlanSnapshot>(plan.InputsJson)!;
        Assert.Equal(21.0975, snapshot.Zones.RaceKm);
        Assert.Equal("threshold", snapshot.Assessment!.Limiters![0].Area);
        Assert.All(plan.Workouts.Where(w => w.Type != WorkoutType.Race), w => Assert.True(w.TargetDistanceKm <= 22));
        Assert.Equal(21.0975, plan.Workouts.Single(w => w.Type == WorkoutType.Race).TargetDistanceKm);

        var structured = plan.Workouts.Where(w => w.StepsJson is not null).ToList();
        Assert.NotEmpty(structured);
        Assert.All(structured, w => Assert.Contains(" → ", w.Description, StringComparison.Ordinal));
        Assert.Contains(structured, w => w.Title.StartsWith("2 × 1.5 km at half-marathon pace", StringComparison.Ordinal));

        // The diagnosis reached the plan design and is shown to the athlete.
        var design = ScriptedLlm.Context(llm.Requests.Single(r => r.Purpose == "plan_design"));
        Assert.Equal("threshold", design["assessment"]!["limiters"]![0]!["area"]!.GetValue<string>());
        Assert.Contains("at half_marathon", design["rules"]!["session_intensities"]!["race_pace"]!.GetValue<string>(), StringComparison.Ordinal);
        var message = plans.RenderProposal(athlete, plan, (await scope.ServiceProvider.GetRequiredService<AthleteService>().GetActiveGoalAsync(athlete.Id, default))!, result.Warnings);
        Assert.Contains("🩺", message.Text, StringComparison.Ordinal);
        Assert.Contains("threshold (lactate) pace (no runs faster than easy pace in 8 weeks)", message.Text, StringComparison.Ordinal);
        Assert.Contains("Half marathon", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Intervals_hidden_in_an_easy_run_are_sent_back_for_repair()
    {
        var hidden = false;
        JsonObject Disguise(JsonObject s)
        {
            if (hidden || s["type"]!.GetValue<string>() != "easy" || s["km"]!.GetValue<double>() < 6)
            {
                return s;
            }

            hidden = true;
            var km = s["km"]!.GetValue<double>();
            s["steps"] = new JsonArray(
                new JsonObject { ["repeat"] = 1, ["work"] = new JsonObject { ["intensity"] = "easy", ["km"] = km - 4 } },
                new JsonObject { ["repeat"] = 4, ["work"] = new JsonObject { ["intensity"] = "five_k", ["km"] = 1 } });
            return s;
        }

        var llm = new ScriptedLlm((r, ctx) => r.Purpose switch
        {
            "plan_design" => ScriptedLlm.Design(ctx, Disguise),
            "plan_design_repair" => ScriptedLlm.Design(ctx["context"]!, rationale: "repaired"),
            _ => null,
        });
        var (sp, scope, athlete) = await SetupAsync(llm);
        await using var _ = sp;
        await using var __ = scope;

        var result = await scope.ServiceProvider.GetRequiredService<PlanService>().GenerateAsync(athlete, default);

        Assert.Equal("repaired", result.Plan!.Rationale);
        var repair = ScriptedLlm.Context(llm.Requests.Single(r => r.Purpose == "plan_design_repair"));
        Assert.Contains("steps_intensity_not_allowed", repair["violations"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsafe_llm_plan_gets_one_repair_attempt_then_falls_back_to_the_rule_based_plan()
    {
        var llm = new ScriptedLlm((r, ctx) => r.Purpose switch
        {
            "plan_design" => ScriptedLlm.Design(ctx, Doubled),
            "plan_design_repair" => ScriptedLlm.Design(ctx["context"]!, Doubled),
            "plan_rationale" => """{"rationale":"rule plan explained","weekly_focus":[]}""",
            _ => null,
        });
        var (sp, scope, athlete) = await SetupAsync(llm);
        await using var _ = sp;
        await using var __ = scope;

        var result = await scope.ServiceProvider.GetRequiredService<PlanService>().GenerateAsync(athlete, default);

        Assert.Equal("rules", result.Plan!.GeneratedBy);
        Assert.Contains("plan.llm_rejected", result.Warnings);
        Assert.Equal("rule plan explained", result.Plan.Rationale);
        var repair = ScriptedLlm.Context(llm.Requests.Single(r => r.Purpose == "plan_design_repair"));
        Assert.Contains("volume_increase_too_large", repair["violations"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Plan.Workouts, w => w.Description?.StartsWith("AI:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Successful_repair_is_accepted()
    {
        var llm = new ScriptedLlm((r, ctx) => r.Purpose switch
        {
            "plan_design" => ScriptedLlm.Design(ctx, Doubled),
            "plan_design_repair" => ScriptedLlm.Design(ctx["context"]!, rationale: "repaired"),
            _ => null,
        });
        var (sp, scope, athlete) = await SetupAsync(llm);
        await using var _ = sp;
        await using var __ = scope;

        var plan = (await scope.ServiceProvider.GetRequiredService<PlanService>().GenerateAsync(athlete, default)).Plan!;

        Assert.StartsWith("llm:", plan.GeneratedBy, StringComparison.Ordinal);
        Assert.Equal("repaired", plan.Rationale);
    }

    [Fact]
    public async Task Negated_keyword_asks_for_confirmation_instead_of_pausing()
    {
        var llm = new ScriptedLlm((r, _) => r.Purpose == "symptom_triage"
            ? """{"category":"none","is_current":false,"evidence":"không hề đau ngực"}"""
            : null);
        var (sp, scope, athlete) = await SetupAsync(llm);
        await using var _ = sp;
        await using var __ = scope;
        var s = scope.ServiceProvider;
        var plan = (await s.GetRequiredService<PlanService>().GenerateAsync(athlete, default)).Plan!;
        await s.GetRequiredService<PlanService>().ApproveAsync(athlete.Id, plan.Id, default);

        var reply = await s.GetRequiredService<FeedbackService>().RecordNoteAsync(athlete, "Hôm nay chạy ổn, không hề đau ngực hay chóng mặt", false, default);

        var buttons = reply.Buttons!.SelectMany(b => b).Select(b => b.CallbackData).ToList();
        Assert.Contains("sym:cardiac", buttons);
        Assert.Contains("sym:none", buttons);
        Assert.Equal(PlanStatus.Active, (await scope.Db().TrainingPlans.SingleAsync(p => p.Id == plan.Id)).Status);
        Assert.False((await scope.Db().Feedback.SingleAsync(f => f.Notes != null)).RedFlag);

        // The athlete confirms after all: the plan is paused.
        await s.GetRequiredService<FeedbackService>().ReportCategoryAsync(athlete, SymptomCategory.Cardiac, default);
        Assert.Equal(PlanStatus.Paused, (await scope.Db().TrainingPlans.SingleAsync(p => p.Id == plan.Id)).Status);
    }

    [Fact]
    public async Task Symptom_found_only_by_the_llm_pauses_immediately()
    {
        var llm = new ScriptedLlm((r, _) => r.Purpose == "symptom_triage"
            ? """{"category":"injury","is_current":true,"evidence":"đầu gối nhói khi xuống dốc"}"""
            : null);
        var (sp, scope, athlete) = await SetupAsync(llm);
        await using var _ = sp;
        await using var __ = scope;
        var s = scope.ServiceProvider;
        var plan = (await s.GetRequiredService<PlanService>().GenerateAsync(athlete, default)).Plan!;
        await s.GetRequiredService<PlanService>().ApproveAsync(athlete.Id, plan.Id, default);
        const string message = "Đầu gối trái nhói mỗi lần xuống dốc từ hôm qua";
        Assert.False(SymptomDetector.Detect(message).IsRedFlag); // keywords alone would miss it

        var reply = await s.GetRequiredService<FeedbackService>().RecordNoteAsync(athlete, message, false, default);

        Assert.Contains("🛑", reply.Text, StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Paused, (await scope.Db().TrainingPlans.SingleAsync(p => p.Id == plan.Id)).Status);
        Assert.Equal(SymptomCategory.Injury, (await scope.Db().Feedback.SingleAsync(f => f.Notes != null)).Symptom);
    }

    [Fact]
    public async Task Llm_progression_is_a_separate_major_proposal_and_only_on_green_days()
    {
        var (sp, scope, athlete) = await SetupAsync(new ScriptedLlm((_, _) => null));
        await using var _ = sp;
        await using var __ = scope;
        var s = scope.ServiceProvider;
        var plan = (await s.GetRequiredService<PlanService>().GenerateAsync(athlete, default)).Plan!;
        await s.GetRequiredService<PlanService>().ApproveAsync(athlete.Id, plan.Id, default);
        var today = new DateOnly(2026, 10, 25);
        var easy = plan.Workouts.Where(w => w.WeekIndex == 0 && w.Type == WorkoutType.Easy).OrderBy(w => w.Date).First();
        var originalKm = easy.TargetDistanceKm;
        var increase = new ProposedChange(easy.Id, default, ChangeAction.IncreaseDistance, WorkoutType.Rest, WorkoutType.Easy, 0, easy.TargetDistanceKm + 1);
        var recommendations = s.GetRequiredService<RecommendationService>();

        var amber = await recommendations.CreateAsync(athlete, plan, [], [increase],
            new ReadinessAssessment(ReadinessLevel.Amber, Confidence.High, [], []), "llm_easing", "s", null, today, default);
        Assert.Empty(amber);

        var green = await recommendations.CreateAsync(athlete, plan, [], [increase],
            new ReadinessAssessment(ReadinessLevel.Green, Confidence.High, [], []), "llm_easing", "s", null, today, default);
        var rec = Assert.Single(green);
        Assert.Equal("llm_progression", rec.Trigger);
        Assert.Equal(ChangeMagnitude.Major, rec.Magnitude);
        Assert.Equal(RecommendationStatus.Proposed, rec.Status);
        Assert.Equal(0, await scope.Db().PlanChanges.CountAsync());

        Assert.Equal(originalKm, easy.TargetDistanceKm); // nothing changes before approval

        Assert.Equal(DecisionOutcome.Done, (await recommendations.ApproveAsync(athlete, rec.Id, default)).Outcome);
        var stored = await scope.Db().PlannedWorkouts.AsNoTracking().SingleAsync(w => w.Id == easy.Id);
        Assert.Equal(originalKm + 1, stored.TargetDistanceKm);
        Assert.Equal("athlete", (await scope.Db().PlanChanges.SingleAsync()).AppliedBy);
    }
}
