using System.Text.Json.Nodes;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CoachBot.IntegrationTests;

/// <summary>The LLM decides; these tests check what code does with its answers, and that a bad answer writes nothing.</summary>
[Collection(PostgresCollection.Name)]
public class LlmIntegrationTests(PostgresFixture pg)
{
    // Wednesday 21 Oct 2026, 07:00 in Ho Chi Minh City. Plans start on Monday 26 Oct.
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    // Tuesday 27 Oct 2026, 12:00 local: the morning run of week 1 has been synced.
    private static readonly DateTimeOffset TuesdayNoon = new(2026, 10, 27, 5, 0, 0, TimeSpan.Zero);

    private sealed record Setup(ServiceProvider Sp, AsyncServiceScope Scope, Athlete Athlete, FakeTimeProvider Time, CapturingNotifier Notifier) : IAsyncDisposable
    {
        public IServiceProvider S => Scope.ServiceProvider;

        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();
            await Sp.DisposeAsync();
        }
    }

    private async Task<Setup> SetupAsync(ScriptedLlm llm, string scenario = "normal", RaceGoal? goal = null)
    {
        var time = new FakeTimeProvider(Start);
        var notifier = new CapturingNotifier();
        var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, notifier, scenario, llm);
        var scope = sp.CreateAsyncScope();
        var athletes = scope.ServiceProvider.GetRequiredService<AthleteService>();
        var athlete = await athletes.GetOrCreatePrimaryAsync(default);
        goal ??= new RaceGoal { RaceDate = new DateOnly(2027, 3, 7), GoalTimeSeconds = 4 * 3600, Name = "Hanoi Marathon", DistanceKm = 42.195 };
        await athletes.SetGoalAsync(athlete.Id, goal.RaceDate, goal.GoalTimeSeconds, goal.Name, goal.DistanceKm, default);
        await scope.ServiceProvider.GetRequiredService<SyncService>().SyncAsync(athlete, 56, 35, default);
        return new Setup(sp, scope, athlete, time, notifier);
    }

    /// <summary>Generates and approves a plan with the scripted defaults.</summary>
    private static async Task<TrainingPlan> ActivePlanAsync(Setup t)
    {
        var plans = t.S.GetRequiredService<PlanService>();
        var plan = (await plans.GenerateAsync(t.Athlete, default)).Plan!;
        await plans.ApproveAsync(t.Athlete.Id, plan.Id, default);
        return plan;
    }

    [Fact]
    public async Task Llm_designed_plan_is_stored_with_its_pace_table()
    {
        var llm = new ScriptedLlm();
        await using var t = await SetupAsync(llm);

        var result = await t.S.GetRequiredService<PlanService>().GenerateAsync(t.Athlete, default);

        var plan = result.Plan!;
        Assert.Equal("llm:scripted", plan.GeneratedBy);
        Assert.Equal("AI rationale", plan.Rationale);
        Assert.Equal("[]", plan.WarningsJson);
        Assert.Equal(PlanStatus.PendingApproval, plan.Status);
        Assert.Equal(new DateOnly(2026, 10, 26), plan.StartDate);

        Assert.Equal(new PaceRange(360, 400), PlanSnapshot.PacesOf(plan).PaceFor(Intensity.Easy));
        var easy = plan.Workouts.First(w => w.Type == WorkoutType.Easy);
        Assert.Equal((360, 400, 120, 145), (easy.PaceMinSecPerKm!.Value, easy.PaceMaxSecPerKm!.Value, easy.HrMin!.Value, easy.HrMax!.Value));
        Assert.All(plan.Workouts, w => Assert.False(string.IsNullOrEmpty(w.Title)));
        Assert.Contains(plan.Workouts, w => w.Description!.StartsWith("AI: relaxed", StringComparison.Ordinal));
        Assert.Equal(new DateOnly(2027, 3, 7), plan.Workouts.Single(w => w.Type == WorkoutType.Race).Date);

        // The LLM gets the data and the plan window, and no rule-made limits, guidelines or baseline.
        var context = ScriptedLlm.Context(llm.Requests.Single(r => r.Purpose == "plan_design"));
        Assert.Equal("2026-10-26", context["plan_window"]!["first_monday"]!.GetValue<string>());
        Assert.Equal(19, context["plan_window"]!["weeks"]!.GetValue<int>());
        Assert.NotNull(context["fitness"]!["weekly_km_oldest_first"]);
        Assert.NotNull(context["assessment"]);
        Assert.Null(context["limits"]);
        Assert.Null(context["guidelines"]);
        Assert.Null(context["baseline_plan"]);

        Assert.Equal(new[] { "ok", "ok" }, await t.Scope.Db().LlmCalls.OrderBy(c => c.CreatedAt).Select(c => c.Status).ToListAsync());
    }

    /// <summary>Gives every quality session a structure: warm-up, 2 repeats, cool-down.</summary>
    private static JsonObject Structured(JsonObject s)
    {
        var (title, intensity) = s["type"]!.GetValue<string>() switch
        {
            "tempo" => ("2 × 1.5 km at half-marathon pace", "race_pace"),
            _ => ((string?)null, (string?)null),
        };
        if (title is null)
        {
            return s;
        }

        s["type"] = "race_pace";
        s["title"] = title;
        s["steps"] = new JsonArray(
            new JsonObject { ["repeat"] = 1, ["work"] = new JsonObject { ["intensity"] = "easy", ["km"] = 2 } },
            new JsonObject
            {
                ["repeat"] = 2,
                ["work"] = new JsonObject { ["intensity"] = intensity, ["km"] = 1.5 },
                ["rest"] = new JsonObject { ["intensity"] = "recovery", ["km"] = 0.5 },
            },
            new JsonObject { ["repeat"] = 1, ["work"] = new JsonObject { ["intensity"] = "easy", ["km"] = 2 } });
        return s;
    }

    [Fact]
    public async Task Half_marathon_goal_gets_a_diagnosis_and_structured_workouts()
    {
        var llm = new ScriptedLlm((r, ctx) => r.Purpose == "plan_design" ? ScriptedLlm.Design(ctx, Structured) : ScriptedLlm.Default(r, ctx));
        var goal = new RaceGoal { RaceDate = new DateOnly(2027, 1, 24), GoalTimeSeconds = 6600, Name = "VnExpress Half", DistanceKm = 21.0975 };
        await using var t = await SetupAsync(llm, goal: goal);
        var plans = t.S.GetRequiredService<PlanService>();

        var plan = (await plans.GenerateAsync(t.Athlete, default)).Plan!;

        var snapshot = PlanSnapshot.Of(plan)!;
        Assert.Equal(21.0975, snapshot.RaceKm);
        Assert.Equal("threshold", snapshot.Assessment!.Limiters![0].Area);
        Assert.Equal(21.0975, plan.Workouts.Single(w => w.Type == WorkoutType.Race).TargetDistanceKm);

        // "race_pace" is half-marathon pace for this goal, and the step paces come from the coach's table.
        var structured = plan.Workouts.First(w => w.StepsJson is not null);
        var blocks = WorkoutStructure.Parse(structured.StepsJson)!;
        Assert.Equal(Intensity.HalfMarathon, blocks[1].Work.Intensity);
        Assert.Equal(320, blocks[1].Work.PaceFast);
        Assert.Equal((320, 150), (structured.PaceMinSecPerKm!.Value, structured.HrMin!.Value));
        Assert.Equal(8, structured.TargetDistanceKm);
        Assert.Contains(" → ", structured.Description, StringComparison.Ordinal);
        Assert.StartsWith("2 × 1.5 km at half-marathon pace", structured.Title, StringComparison.Ordinal);

        // The diagnosis reached the plan design and is shown to the athlete with the coach's prediction.
        var design = ScriptedLlm.Context(llm.Requests.Single(r => r.Purpose == "plan_design"));
        Assert.Equal("threshold", design["assessment"]!["limiters"]![0]!["area"]!.GetValue<string>());
        var message = plans.RenderProposal(t.Athlete, plan, (await t.S.GetRequiredService<AthleteService>().GetActiveGoalAsync(t.Athlete.Id, default))!);
        Assert.Contains("🩺", message.Text, StringComparison.Ordinal);
        Assert.Contains("threshold (lactate) pace (no runs faster than easy pace in 8 weeks)", message.Text, StringComparison.Ordinal);
        Assert.Contains("Half marathon", message.Text, StringComparison.Ordinal);
        Assert.Contains("4:05:00", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_generation_fails_without_writing_when_the_llm_fails()
    {
        var llm = new ScriptedLlm((r, ctx) => r.Purpose == "plan_design" ? null : ScriptedLlm.Default(r, ctx));
        await using var t = await SetupAsync(llm);

        var ex = await Assert.ThrowsAsync<LlmException>(() => t.S.GetRequiredService<PlanService>().GenerateAsync(t.Athlete, default));

        Assert.Equal(LlmFailure.Empty, ex.Reason);
        Assert.Equal(0, await t.Scope.Db().TrainingPlans.CountAsync());
        Assert.Equal("empty", (await t.Scope.Db().LlmCalls.SingleAsync(c => c.Purpose == "plan_design")).Status);
    }

    [Fact]
    public async Task A_design_that_does_not_fit_fails_without_writing()
    {
        static JsonObject SameDay(JsonObject s)
        {
            if (s["type"]!.GetValue<string>() == "tempo")
            {
                s["day"] = "tue"; // two sessions on Tuesday
            }

            return s;
        }

        var llm = new ScriptedLlm((r, ctx) => r.Purpose == "plan_design" ? ScriptedLlm.Design(ctx, SameDay) : ScriptedLlm.Default(r, ctx));
        await using var t = await SetupAsync(llm);

        var ex = await Assert.ThrowsAsync<LlmException>(() => t.S.GetRequiredService<PlanService>().GenerateAsync(t.Athlete, default));

        Assert.Equal(LlmFailure.Integrity, ex.Reason);
        Assert.Contains("two_sessions_same_day", ex.Detail, StringComparison.Ordinal);
        Assert.Equal(0, await t.Scope.Db().TrainingPlans.CountAsync());
        var call = await t.Scope.Db().LlmCalls.SingleAsync(c => c.Purpose == "plan_design");
        Assert.Equal("integrity", call.Status);
        Assert.NotNull(call.Response);
    }

    [Fact]
    public async Task Run_analysis_matches_the_named_session_and_splits_changes_into_applied_and_proposed()
    {
        var llm = new ScriptedLlm((r, ctx) =>
        {
            if (r.Purpose != "run_analysis")
            {
                return ScriptedLlm.Default(r, ctx);
            }

            var sessions = ctx["plan"]!["sessions"]!.AsArray();
            string Id(string type) => sessions.First(s => s!["type"]!.GetValue<string>() == type && s["status"]!.GetValue<string>() == "planned")!["workout_id"]!.GetValue<string>();
            string Date(string type) => sessions.First(s => s!["type"]!.GetValue<string>() == type)!["date"]!.GetValue<string>();
            return new JsonObject
            {
                ["summary"] = "Heavy legs today.",
                ["matched_workout_id"] = ScriptedLlm.PlannedSessionOn(ctx, ctx["run"]!["date"]!.GetValue<string>()),
                ["pace_status"] = "slower",
                ["hr_status"] = "above",
                ["readiness"] = ScriptedLlm.Readiness("amber", "HRV is down and resting HR is up.", "HRV 42 ms"),
                ["changes"] = new JsonArray(
                    ScriptedLlm.Change(Id("tempo"), Date("tempo"), "easy", 6, needsApproval: false),
                    ScriptedLlm.Change(Id("long"), Date("long"), "long", 12, needsApproval: true)),
                ["symptom_concerns"] = new JsonArray(),
            }.ToJsonString();
        });
        await using var t = await SetupAsync(llm);
        var plan = await ActivePlanAsync(t);
        t.Time.SetUtcNow(TuesdayNoon);
        await t.S.GetRequiredService<SyncService>().SyncAsync(t.Athlete, 3, 7, default);

        Assert.Equal(1, await t.S.GetRequiredService<AnalysisService>().AnalyzePendingAsync(t.Athlete, default));

        var db = t.Scope.Db();
        var analysis = await db.RunAnalyses.SingleAsync();
        var tuesday = plan.Workouts.Single(w => w.Date == new DateOnly(2026, 10, 27));
        Assert.Equal(tuesday.Id, analysis.PlannedWorkoutId);
        Assert.Equal((ReadinessLevel.Amber, "slower", "above"), (analysis.Readiness, analysis.PaceStatus, analysis.HrStatus));
        Assert.Equal(1.0, analysis.DistanceRatio);
        Assert.Equal(WorkoutStatus.Completed, tuesday.Status);
        Assert.Equal(analysis.ActivityId, tuesday.CompletedActivityId);

        var check = await db.ReadinessChecks.SingleAsync();
        Assert.Equal((new DateOnly(2026, 10, 27), ReadinessLevel.Amber, "run_analysis"), (check.Date, check.Level, check.Source));

        var applied = await db.Recommendations.SingleAsync(r => r.Status == RecommendationStatus.AutoApplied);
        var proposed = await db.Recommendations.SingleAsync(r => r.Status == RecommendationStatus.Proposed);
        Assert.Equal((ChangeMagnitude.Minor, ChangeMagnitude.Major), (applied.Magnitude, proposed.Magnitude));
        var thursday = plan.Workouts.Single(w => w.Date == new DateOnly(2026, 10, 29));
        Assert.Equal((WorkoutType.Easy, 6.0, 360), (thursday.Type, thursday.TargetDistanceKm, thursday.PaceMinSecPerKm!.Value));
        var longRun = plan.Workouts.Single(w => w.Date == new DateOnly(2026, 11, 1));
        Assert.Equal(14, longRun.TargetDistanceKm); // waits for approval
        Assert.Equal(1, await db.PlanChanges.CountAsync());

        var message = t.Notifier.Messages.Last();
        Assert.Contains("🟠", message.Text, StringComparison.Ordinal);
        Assert.Contains("→", message.Text, StringComparison.Ordinal);
        var buttons = message.Buttons!.SelectMany(r => r).Select(b => b.CallbackData).ToList();
        Assert.Contains($"rec:undo:{applied.Id}", buttons);
        Assert.Contains($"rec:approve:{proposed.Id}", buttons);
        Assert.Contains(buttons, b => b.StartsWith("rpe:", StringComparison.Ordinal));
        Assert.Equal("fatigue:2026-10-27", (await db.Alerts.SingleAsync(a => a.Type == AlertType.Fatigue)).DedupKey);

        // Approving applies exactly what was shown; undo restores the auto-applied change.
        var recommendations = t.S.GetRequiredService<RecommendationService>();
        Assert.Equal(DecisionOutcome.Done, (await recommendations.ApproveAsync(t.Athlete, proposed.Id, default)).Outcome);
        Assert.Equal(12, (await db.PlannedWorkouts.AsNoTracking().SingleAsync(w => w.Id == longRun.Id)).TargetDistanceKm);
        Assert.Equal(DecisionOutcome.Done, await recommendations.UndoAsync(t.Athlete, applied.Id, default));
        var restored = await db.PlannedWorkouts.AsNoTracking().SingleAsync(w => w.Id == thursday.Id);
        Assert.Equal((WorkoutType.Tempo, 8.0), (restored.Type, restored.TargetDistanceKm));
    }

    [Fact]
    public async Task A_failed_analysis_writes_nothing_and_is_retried_at_the_next_sync()
    {
        var broken = true;
        var llm = new ScriptedLlm((r, ctx) =>
        {
            if (r.Purpose != "run_analysis" || !broken)
            {
                return ScriptedLlm.Default(r, ctx);
            }

            // A change to a session the LLM was not offered: unusable.
            var answer = JsonNode.Parse(ScriptedLlm.Default(r, ctx)!)!.AsObject();
            answer["changes"] = new JsonArray(ScriptedLlm.Change(Guid.NewGuid().ToString(), "2026-10-29", "easy", 6, needsApproval: false));
            return answer.ToJsonString();
        });
        await using var t = await SetupAsync(llm);
        var plan = await ActivePlanAsync(t);
        t.Time.SetUtcNow(TuesdayNoon);
        await t.S.GetRequiredService<SyncService>().SyncAsync(t.Athlete, 3, 7, default);
        var analysis = t.S.GetRequiredService<AnalysisService>();
        var db = t.Scope.Db();

        Assert.Equal(0, await analysis.AnalyzePendingAsync(t.Athlete, default));

        Assert.Equal(0, await db.RunAnalyses.CountAsync());
        Assert.Equal(0, await db.ReadinessChecks.CountAsync());
        Assert.Equal(WorkoutStatus.Planned, (await db.PlannedWorkouts.AsNoTracking().SingleAsync(w => w.Date == new DateOnly(2026, 10, 27) && w.PlanId == plan.Id)).Status);
        Assert.Contains("/sync", t.Notifier.Messages.Last().Text, StringComparison.Ordinal);
        Assert.Equal("integrity", (await db.LlmCalls.SingleAsync(c => c.Purpose == "run_analysis")).Status);

        broken = false;
        Assert.Equal(1, await analysis.AnalyzePendingAsync(t.Athlete, default));
        Assert.Equal(1, await db.RunAnalyses.CountAsync());
        Assert.Single(await db.Alerts.Where(a => a.Type == AlertType.AnalysisFailure).ToListAsync());
        Assert.Equal(new[] { "integrity", "ok" }, await db.LlmCalls.Where(c => c.Purpose == "run_analysis").OrderBy(c => c.CreatedAt).Select(c => c.Status).ToListAsync());
    }

    [Fact]
    public async Task Daily_check_stores_the_verdict_and_is_silent_without_a_plan()
    {
        var llm = new ScriptedLlm((r, ctx) => r.Purpose == "daily_check"
            ? new JsonObject
            {
                ["readiness"] = ScriptedLlm.Readiness("red", "Resting HR is 9 bpm above normal.", "resting HR 58"),
                ["advice"] = "Rest today.",
                ["changes"] = new JsonArray(ScriptedLlm.Change(
                    ScriptedLlm.PlannedSessionOn(ctx, ctx["today"]!.GetValue<string>())!, ctx["today"]!.GetValue<string>(), "rest", 0, needsApproval: false)),
            }.ToJsonString()
            : ScriptedLlm.Default(r, ctx));
        await using var t = await SetupAsync(llm);
        var reports = t.S.GetRequiredService<ReportService>();

        Assert.Null(await reports.DailyCheckAsync(t.Athlete, default));
        Assert.DoesNotContain(llm.Requests, r => r.Purpose == "daily_check");
        Assert.Contains("No readiness check yet", (await reports.TodayAsync(t.Athlete, default)).Text, StringComparison.Ordinal);

        var plan = await ActivePlanAsync(t);
        t.Time.SetUtcNow(new DateTimeOffset(2026, 10, 26, 23, 30, 0, TimeSpan.Zero)); // Tue 27 Oct 06:30
        var message = (await reports.DailyCheckAsync(t.Athlete, default))!;

        Assert.Contains("Rest today.", message.Text, StringComparison.Ordinal);
        Assert.Contains("🔴", message.Text, StringComparison.Ordinal);
        Assert.Contains(message.Buttons!.SelectMany(r => r), b => b.CallbackData.StartsWith("rec:undo:", StringComparison.Ordinal));
        var check = await t.Scope.Db().ReadinessChecks.SingleAsync();
        Assert.Equal((ReadinessLevel.Red, "daily_check"), (check.Level, check.Source));
        Assert.Equal(WorkoutType.Rest, plan.Workouts.Single(w => w.Date == new DateOnly(2026, 10, 27)).Type);
        Assert.Contains("checked", (await reports.TodayAsync(t.Athlete, default)).Text, StringComparison.Ordinal);
    }

    private static ScriptedLlm Triage(string answer) => new((r, ctx) => r.Purpose == "symptom_triage" ? answer : ScriptedLlm.Default(r, ctx));

    [Fact]
    public async Task Triage_can_ask_the_athlete_instead_of_pausing()
    {
        await using var t = await SetupAsync(Triage("""{"category":"cardiac","is_current":false,"action":"ask","evidence":"không hề đau ngực"}"""));
        var plan = await ActivePlanAsync(t);
        var feedback = t.S.GetRequiredService<FeedbackService>();

        var note = await feedback.SaveNoteAsync(t.Athlete, "Hôm nay chạy ổn, không hề đau ngực hay chóng mặt", default);
        var reply = (await feedback.TriageAsync(t.Athlete, note.Id, false, default))!;

        Assert.Contains("không hề đau ngực", reply.Text, StringComparison.Ordinal);
        var buttons = reply.Buttons!.SelectMany(b => b).Select(b => b.CallbackData).ToList();
        Assert.Equal(["sym:cardiac", "sym:none"], buttons);
        Assert.Equal(PlanStatus.Active, (await t.Scope.Db().TrainingPlans.SingleAsync(p => p.Id == plan.Id)).Status);
        Assert.False((await t.Scope.Db().Feedback.SingleAsync(f => f.Notes != null)).RedFlag);

        // The athlete confirms with the button: that pauses the plan without asking the LLM again.
        var triageCalls = t.Scope.Db().LlmCalls.Count(c => c.Purpose == "symptom_triage");
        await feedback.ReportCategoryAsync(t.Athlete, SymptomCategory.Cardiac, default);
        Assert.Equal(PlanStatus.Paused, (await t.Scope.Db().TrainingPlans.SingleAsync(p => p.Id == plan.Id)).Status);
        Assert.Equal(triageCalls, t.Scope.Db().LlmCalls.Count(c => c.Purpose == "symptom_triage"));
    }

    [Fact]
    public async Task Triage_pause_pauses_the_plan_and_marks_the_note()
    {
        await using var t = await SetupAsync(Triage("""{"category":"injury","is_current":true,"action":"pause","evidence":"đầu gối nhói khi xuống dốc"}"""));
        var plan = await ActivePlanAsync(t);
        var feedback = t.S.GetRequiredService<FeedbackService>();

        var note = await feedback.SaveNoteAsync(t.Athlete, "Đầu gối trái nhói mỗi lần xuống dốc từ hôm qua", default);
        var reply = (await feedback.TriageAsync(t.Athlete, note.Id, false, default))!;

        Assert.Contains("🛑", reply.Text, StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Paused, (await t.Scope.Db().TrainingPlans.SingleAsync(p => p.Id == plan.Id)).Status);
        var stored = await t.Scope.Db().Feedback.SingleAsync(f => f.Id == note.Id);
        Assert.Equal((SymptomCategory.Injury, true), (stored.Symptom, stored.RedFlag));
        Assert.Equal("injury: đầu gối nhói khi xuống dốc", (await t.Scope.Db().Alerts.SingleAsync(a => a.Type == AlertType.Symptom)).Message);
    }

    [Fact]
    public async Task Nothing_serious_gets_an_answer_only_when_the_athlete_reported_a_symptom()
    {
        await using var t = await SetupAsync(new ScriptedLlm());
        var feedback = t.S.GetRequiredService<FeedbackService>();

        var note = await feedback.SaveNoteAsync(t.Athlete, "legs heavy after the long run", default);
        Assert.Null(await feedback.TriageAsync(t.Athlete, note.Id, false, default));
        Assert.Contains("Nothing in that looks like a red flag", (await feedback.TriageAsync(t.Athlete, note.Id, true, default))!.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"category":"none","is_current":true,"action":"pause","evidence":"x"}""")]
    [InlineData("""{"category":"injury","is_current":true,"action":"stop","evidence":"x"}""")]
    public async Task A_note_the_llm_cannot_read_stays_saved(string? answer)
    {
        await using var t = await SetupAsync(Triage(answer!));
        var plan = await ActivePlanAsync(t);
        var feedback = t.S.GetRequiredService<FeedbackService>();

        var note = await feedback.SaveNoteAsync(t.Athlete, "chest feels tight", default);
        await Assert.ThrowsAsync<LlmException>(() => feedback.TriageAsync(t.Athlete, note.Id, false, default));

        Assert.Equal("chest feels tight", (await t.Scope.Db().Feedback.SingleAsync(f => f.Id == note.Id)).Notes);
        Assert.Equal(PlanStatus.Active, (await t.Scope.Db().TrainingPlans.SingleAsync(p => p.Id == plan.Id)).Status);
    }
}
