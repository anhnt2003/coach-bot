using System.Text.Json.Nodes;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Services;
using CoachBot.Infrastructure.Coros;
using CoachBot.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CoachBot.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class SchemaMappingTests(PostgresFixture pg)
{
    [Fact]
    public async Task Every_entity_round_trips_through_the_supabase_schema()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, new CapturingNotifier());
        var now = time.GetUtcNow();
        var athlete = new Athlete { Name = "A", TelegramChatId = 42, MaxHr = 188, BirthYear = 1990, LongRunDay = DayOfWeek.Saturday, CreatedAt = now };
        var goal = new RaceGoal { AthleteId = athlete.Id, Name = "Hanoi", RaceDate = new DateOnly(2027, 3, 7), GoalTimeSeconds = 14400, CreatedAt = now };
        var activity = new Activity { AthleteId = athlete.Id, Source = "fake", ExternalId = "x1", StartTime = now, DurationSeconds = 3600, DistanceMeters = 10000, RawJson = """{"a":1}""", ImportedAt = now };
        var plan = new TrainingPlan { AthleteId = athlete.Id, RaceGoalId = goal.Id, Version = 1, Status = PlanStatus.Active, StartDate = new DateOnly(2026, 10, 26), EndDate = goal.RaceDate, InputsJson = "{}", CreatedAt = now };
        var workout = new PlannedWorkout { PlanId = plan.Id, Date = new DateOnly(2026, 10, 27), Phase = PlanPhase.Build, Type = WorkoutType.RacePace, TargetDistanceKm = 12, Title = "MP", CompletedActivityId = activity.Id, CorosSyncStatus = SyncStatus.ManualActionRequired, UpdatedAt = now };
        plan.Workouts.Add(workout);
        var analysis = new RunAnalysis { AthleteId = athlete.Id, ActivityId = activity.Id, PlannedWorkoutId = workout.Id, Readiness = ReadinessLevel.Amber, ReadinessConfidence = Confidence.Medium, CreatedAt = now };
        var rec = new Recommendation { AthleteId = athlete.Id, PlanId = plan.Id, RunAnalysisId = analysis.Id, Trigger = "t", Summary = "s", ChangesJson = "[]", Magnitude = ChangeMagnitude.Major, Status = RecommendationStatus.Proposed, CreatedAt = now, ExpiresAt = now.AddHours(48) };

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.Db();
            db.AddRange(athlete, goal, activity, plan, analysis, rec);
            db.Add(new CorosConnection { AthleteId = athlete.Id, McpUrl = "u", Issuer = "i", ClientId = "c", CapabilitiesJson = """["a"]""", CreatedAt = now, UpdatedAt = now });
            db.Add(new DailyMetric { AthleteId = athlete.Id, Date = new DateOnly(2026, 10, 21), HrvMs = 55.5, Vo2Max = 48.2, Source = "fake", UpdatedAt = now });
            db.Add(new AthleteFeedback { AthleteId = athlete.Id, ActivityId = activity.Id, Date = new DateOnly(2026, 10, 21), Rpe = 7, Symptom = SymptomCategory.Injury, RedFlag = true, CreatedAt = now });
            db.Add(new PlanChange { PlanId = plan.Id, PlannedWorkoutId = workout.Id, RecommendationId = rec.Id, BeforeJson = "{}", AfterJson = "{}", Reason = "r", AppliedBy = "auto", AppliedAt = now });
            db.Add(new Alert { AthleteId = athlete.Id, Type = AlertType.Fatigue, Severity = AlertSeverity.Warning, Message = "m", DedupKey = "k", CreatedAt = now });
            db.Add(new JobRun { JobName = "sync", RunKey = "k", StartedAt = now, Status = "succeeded" });
            db.Add(new LlmCall { AthleteId = athlete.Id, Purpose = "plan_design", Model = "scripted", ContextJson = """{"k":1}""", Response = "{", Status = "schema", Error = "e", DurationMs = 12, CreatedAt = now });
            db.Add(new ReadinessCheck { AthleteId = athlete.Id, Date = new DateOnly(2026, 10, 21), Level = ReadinessLevel.Amber, Confidence = Confidence.High, Summary = "s", ConcernsJson = """["hrv"]""", Source = "daily_check", CreatedAt = now });
            await db.SaveChangesAsync();
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var db = scope.Db();
            var a = await db.Athletes.SingleAsync();
            Assert.Equal(DayOfWeek.Saturday, a.LongRunDay);
            Assert.Equal(WorkoutType.RacePace, (await db.PlannedWorkouts.SingleAsync()).Type);
            Assert.Equal(48.2, (await db.DailyMetrics.SingleAsync()).Vo2Max);
            Assert.Equal(SymptomCategory.Injury, (await db.Feedback.SingleAsync()).Symptom);
            Assert.Equal(1, await db.PlanChanges.CountAsync());
            Assert.Equal(1, await db.JobRuns.CountAsync());
            Assert.Equal("""["a"]""", (await db.CorosConnections.SingleAsync()).CapabilitiesJson);
            var call = await db.LlmCalls.SingleAsync();
            Assert.Equal(("schema", "{", """{"k": 1}"""), (call.Status, call.Response, call.ContextJson));
            var check = await db.ReadinessChecks.SingleAsync();
            Assert.Equal((ReadinessLevel.Amber, Confidence.High, """["hrv"]"""), (check.Level, check.Confidence, check.ConcernsJson));

            // Deleting the athlete removes all their data (cascade), including plans that reference goals.
            db.Athletes.Remove(a);
            await db.SaveChangesAsync();
            Assert.Equal(0, await db.Activities.CountAsync());
            Assert.Equal(0, await db.TrainingPlans.CountAsync());
            Assert.Equal(0, await db.LlmCalls.CountAsync());
            Assert.Equal(0, await db.ReadinessChecks.CountAsync());
        }
    }

    [Fact]
    public async Task Alert_dedup_key_is_unique_per_athlete()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));
        var notifier = new CapturingNotifier();
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, notifier);
        await using var scope = sp.CreateAsyncScope();
        var athlete = await scope.ServiceProvider.GetRequiredService<AthleteService>().GetOrCreatePrimaryAsync(default);
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();

        Assert.True(await alerts.RaiseAsync(athlete.Id, AlertType.Fatigue, AlertSeverity.Warning, "m", "fatigue:2026-10-21", default));
        Assert.False(await alerts.RaiseAsync(athlete.Id, AlertType.Fatigue, AlertSeverity.Warning, "m", "fatigue:2026-10-21", default));
        Assert.Single(notifier.Messages);
    }
}

[Collection(PostgresCollection.Name)]
public class EndToEndTests(PostgresFixture pg)
{
    // Wednesday 21 Oct 2026, 07:00 in Ho Chi Minh City.
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sync_is_idempotent()
    {
        var time = new FakeTimeProvider(Start);
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, new CapturingNotifier());
        await using var scope = sp.CreateAsyncScope();
        var athlete = await scope.ServiceProvider.GetRequiredService<AthleteService>().GetOrCreatePrimaryAsync(default);
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();

        var first = await sync.SyncAsync(athlete, 56, 35, default);
        var second = await sync.SyncAsync(athlete, 56, 35, default);

        Assert.True(first.Success);
        Assert.True(first.NewActivities > 25);
        Assert.Equal(0, second.NewActivities);
        Assert.Equal(first.NewActivities, await scope.Db().Activities.CountAsync());
        Assert.Equal(36, await scope.Db().DailyMetrics.CountAsync());
        Assert.NotNull((await scope.Db().DailyMetrics.SingleAsync(m => m.Date == new DateOnly(2026, 10, 21))).ThresholdPaceSecPerKm);
    }

    [Fact]
    public async Task Scheduled_jobs_wait_until_coros_is_connected()
    {
        var time = new FakeTimeProvider(Start);
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, new CapturingNotifier());
        var jobs = new CronJobService(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new JobsOptions()),
            sp.GetRequiredService<IOptions<CoachOptions>>(), time, NullLogger<CronJobService>.Instance);
        var syncJob = jobs.Jobs.Single(j => j.Name == "sync");
        await using var scope = sp.CreateAsyncScope();
        var athlete = await scope.ServiceProvider.GetRequiredService<AthleteService>().GetOrCreatePrimaryAsync(default);

        await jobs.RunAsync(syncJob, Start, default);
        Assert.False(await scope.Db().Activities.AnyAsync());

        var now = time.GetUtcNow();
        scope.Db().Add(new CorosConnection { AthleteId = athlete.Id, McpUrl = "u", Issuer = "i", ClientId = "c", EncryptedRefreshToken = "t", CreatedAt = now, UpdatedAt = now });
        await scope.Db().SaveChangesAsync();
        await jobs.RunAsync(syncJob, Start.AddHours(1), default);
        Assert.True(await scope.Db().Activities.AnyAsync());
    }

    [Fact]
    public async Task A_failed_job_tells_the_athlete_why_and_how_to_run_it_now()
    {
        var time = new FakeTimeProvider(Start);
        var notifier = new CapturingNotifier();
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, notifier);
        var jobs = new CronJobService(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new JobsOptions()),
            sp.GetRequiredService<IOptions<CoachOptions>>(), time, NullLogger<CronJobService>.Instance);
        await using var scope = sp.CreateAsyncScope();
        var athlete = await scope.ServiceProvider.GetRequiredService<AthleteService>().GetOrCreatePrimaryAsync(default);
        var now = time.GetUtcNow();
        scope.Db().Add(new CorosConnection { AthleteId = athlete.Id, McpUrl = "u", Issuer = "i", ClientId = "c", EncryptedRefreshToken = "t", CreatedAt = now, UpdatedAt = now });
        await scope.Db().SaveChangesAsync();

        var failing = new CoachJob("daily_check", Cronos.CronExpression.Parse("30 6 * * *"),
            (_, _, _) => throw new LlmException("daily_check", LlmFailure.Timeout, "no answer"));
        await jobs.RunAsync(failing, Start, default);

        var run = await scope.Db().JobRuns.SingleAsync();
        Assert.Equal("failed", run.Status);
        var text = Assert.Single(notifier.Messages).Text;
        Assert.Contains("/daily", text, StringComparison.Ordinal);
        Assert.Contains("did not answer in time", text, StringComparison.Ordinal);
    }

    /// <summary>A tired run: red readiness, Thursday's tempo eased at once, Sunday's long run shortened if the athlete agrees.</summary>
    private static string Fatigued(JsonNode ctx)
    {
        var sessions = ctx["plan"]!["sessions"]!.AsArray();
        JsonNode First(string type) => sessions.First(s => s!["type"]!.GetValue<string>() == type && s["status"]!.GetValue<string>() == "planned")!;
        string Id(JsonNode s) => s["workout_id"]!.GetValue<string>();
        string Date(JsonNode s) => s["date"]!.GetValue<string>();
        var tempo = First("tempo");
        var longRun = First("long");
        return new JsonObject
        {
            ["summary"] = "You ran it, but everything says you are tired.",
            ["matched_workout_id"] = ScriptedLlm.PlannedSessionOn(ctx, ctx["run"]!["date"]!.GetValue<string>()),
            ["pace_status"] = "slower",
            ["hr_status"] = "above",
            ["readiness"] = ScriptedLlm.Readiness("red", "HRV is far below normal and resting HR is up.", "HRV 42 ms", "resting HR 58"),
            ["changes"] = new JsonArray(
                ScriptedLlm.Change(Id(tempo), Date(tempo), "easy", 6, needsApproval: false),
                ScriptedLlm.Change(Id(longRun), Date(longRun), "long", 12, needsApproval: true)),
            ["symptom_concerns"] = new JsonArray(),
        }.ToJsonString();
    }

    [Fact]
    public async Task Full_coaching_loop_with_fatigue_approval_symptom_pause_and_resume()
    {
        var time = new FakeTimeProvider(Start);
        var notifier = new CapturingNotifier();
        var llm = new ScriptedLlm((r, ctx) => r.Purpose switch
        {
            "run_analysis" => Fatigued(ctx),
            "symptom_triage" => """{"category":"cardiac","is_current":true,"evidence":"chest pain"}""",
            _ => ScriptedLlm.Default(r, ctx),
        });
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, notifier, "fatigued", llm);
        Guid planId;

        // 1. Onboarding: goal, history backfill, plan proposal, approval, push to (fake) COROS.
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athletes = s.GetRequiredService<AthleteService>();
            var athlete = await athletes.LinkTelegramAsync(42, "Anh", default);
            await athletes.SetGoalAsync(athlete.Id, new DateOnly(2027, 3, 7), 4 * 3600, "Hanoi Marathon", 42.195, default);
            Assert.True((await s.GetRequiredService<SyncService>().SyncAsync(athlete, 56, 35, default)).Success);

            var plans = s.GetRequiredService<PlanService>();
            var result = await plans.GenerateAsync(athlete, default);
            Assert.Null(result.FailureKey);
            var plan = result.Plan!;
            planId = plan.Id;
            Assert.Equal(PlanStatus.PendingApproval, plan.Status);
            Assert.NotEmpty(PlanSnapshot.PacesOf(plan).Entries);
            Assert.All(plan.Workouts, w => Assert.False(string.IsNullOrEmpty(w.Title)));

            var proposal = plans.RenderProposal(athlete, plan, (await athletes.GetActiveGoalAsync(athlete.Id, default))!);
            Assert.Contains($"plan:approve:{plan.Id}", proposal.Buttons!.SelectMany(r => r).Select(b => b.CallbackData));

            Assert.NotNull((await plans.ApproveAsync(athlete.Id, plan.Id, default)).Plan);
            var push = await s.GetRequiredService<CorosPushService>().PushUpcomingAsync(athlete, default);
            Assert.True(push.Pushed >= 1);
            Assert.Equal(push.Pushed, FakeCorosGateway.PushedWorkouts.Count);
        }

        // 2. Tuesday of week 1, after the morning run: the coach eases Thursday at once and proposes a shorter long run.
        time.SetUtcNow(new DateTimeOffset(2026, 10, 27, 5, 0, 0, TimeSpan.Zero));
        Recommendation applied, proposed;
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athlete = await scope.Db().Athletes.SingleAsync();
            var sync = await s.GetRequiredService<SyncService>().SyncAsync(athlete, 3, 7, default);
            Assert.Equal(3, sync.NewRunIds.Count); // Sat + Sun before the plan started, and today's run
            Assert.True((await s.GetRequiredService<CorosPushService>().PushUpcomingAsync(athlete, default)).Pushed >= 1);
            var messagesBefore = notifier.Messages.Count;

            // Only today's run is recent enough to analyze.
            Assert.Equal(1, await s.GetRequiredService<AnalysisService>().AnalyzePendingAsync(athlete, default));
            Assert.Equal(messagesBefore + 1, notifier.Messages.Count);

            var analysis = await scope.Db().RunAnalyses.SingleAsync();
            Assert.Equal(ReadinessLevel.Red, analysis.Readiness);
            Assert.Equal(WorkoutStatus.Completed, (await scope.Db().PlannedWorkouts.SingleAsync(w => w.Id == analysis.PlannedWorkoutId)).Status);

            applied = await scope.Db().Recommendations.SingleAsync(r => r.Status == RecommendationStatus.AutoApplied);
            proposed = await scope.Db().Recommendations.SingleAsync(r => r.Status == RecommendationStatus.Proposed);
            Assert.Equal("run_analysis", applied.Trigger);
            var thursday = await scope.Db().PlannedWorkouts.SingleAsync(w => w.PlanId == planId && w.Date == new DateOnly(2026, 10, 29));
            Assert.Equal((WorkoutType.Easy, 6.0), (thursday.Type, thursday.TargetDistanceKm));
            Assert.True(thursday.CorosDirty, "already on the COROS calendar, so it is pushed again");
            Assert.Single(await scope.Db().PlanChanges.ToListAsync()); // the proposal waits for approval

            var message = notifier.Messages.Last();
            Assert.Contains("🔴", message.Text, StringComparison.Ordinal);
            var buttons = message.Buttons!.SelectMany(r => r).Select(b => b.CallbackData).ToList();
            Assert.Contains($"rec:approve:{proposed.Id}", buttons);
            Assert.Contains($"rec:undo:{applied.Id}", buttons);
            Assert.Contains(buttons, b => b.StartsWith("rpe:", StringComparison.Ordinal));
            Assert.Single(await scope.Db().Alerts.Where(a => a.Type == AlertType.Fatigue).ToListAsync());
        }

        // 3. Athlete approves: the change is applied with an audit trail and pushed again.
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athlete = await scope.Db().Athletes.SingleAsync();
            Assert.Equal(DecisionOutcome.Done, (await s.GetRequiredService<RecommendationService>().ApproveAsync(athlete, proposed.Id, default)).Outcome);

            var change = await scope.Db().PlanChanges.SingleAsync(c => c.RecommendationId == proposed.Id);
            Assert.Equal(12, (await scope.Db().PlannedWorkouts.SingleAsync(w => w.Id == change.PlannedWorkoutId)).TargetDistanceKm);
            Assert.True((await s.GetRequiredService<CorosPushService>().PushUpcomingAsync(athlete, default)).Updated >= 1);
        }

        // 4. The change applied at once can be undone from the audit trail.
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athlete = await scope.Db().Athletes.SingleAsync();
            Assert.Equal(DecisionOutcome.Done, await s.GetRequiredService<RecommendationService>().UndoAsync(athlete, applied.Id, default));
            var thursday = await scope.Db().PlannedWorkouts.AsNoTracking().SingleAsync(w => w.PlanId == planId && w.Date == new DateOnly(2026, 10, 29));
            Assert.Equal((WorkoutType.Tempo, 8.0), (thursday.Type, thursday.TargetDistanceKm));
        }

        // 5. A red-flag symptom pauses the plan immediately; COROS pushes stop; resume needs confirmation.
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athlete = await scope.Db().Athletes.SingleAsync();
            var feedback = s.GetRequiredService<FeedbackService>();

            var reply = await feedback.RecordNoteAsync(athlete, "felt some chest pain near the end", false, default);
            Assert.Contains("🚨", reply.Text, StringComparison.Ordinal);
            Assert.Equal(PlanStatus.Paused, (await scope.Db().TrainingPlans.SingleAsync(p => p.Id == planId)).Status);
            Assert.True((await scope.Db().Feedback.OrderBy(f => f.CreatedAt).LastAsync()).RedFlag);
            Assert.Equal("plan_paused", (await s.GetRequiredService<CorosPushService>().PushUpcomingAsync(athlete, default)).SkippedReason);

            var confirm = await feedback.RequestResumeAsync(athlete, default);
            Assert.Contains("resume:confirm", confirm.Buttons!.SelectMany(r => r).Select(b => b.CallbackData));
            await feedback.ConfirmResumeAsync(athlete, default);
            Assert.Equal(PlanStatus.Active, (await scope.Db().TrainingPlans.SingleAsync(p => p.Id == planId)).Status);
        }

        // 6. Reports render with every message key present (strict catalog) and mark missed sessions.
        time.SetUtcNow(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athlete = await scope.Db().Athletes.SingleAsync();
            var reports = s.GetRequiredService<ReportService>();
            Assert.NotNull(await reports.DailyCheckAsync(athlete, default));
            Assert.NotNull(await reports.WeeklyReportAsync(athlete, default));
            Assert.Contains("v1", (await reports.StatusAsync(athlete, default)).Text, StringComparison.Ordinal);
            Assert.NotEmpty((await reports.TodayAsync(athlete, default)).Text);
            Assert.True(await scope.Db().PlannedWorkouts.AnyAsync(w => w.Status == WorkoutStatus.Missed));
        }
    }

    [Fact]
    public async Task Unanswered_proposals_expire_and_leave_the_plan_unchanged()
    {
        var time = new FakeTimeProvider(Start);
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, new CapturingNotifier());
        await using var scope = sp.CreateAsyncScope();
        var s = scope.ServiceProvider;
        var athletes = s.GetRequiredService<AthleteService>();
        var athlete = await athletes.GetOrCreatePrimaryAsync(default);
        await athletes.SetGoalAsync(athlete.Id, new DateOnly(2027, 3, 7), null, "M", 42.195, default);
        await s.GetRequiredService<SyncService>().SyncAsync(athlete, 56, 35, default);
        var plan = (await s.GetRequiredService<PlanService>().GenerateAsync(athlete, default)).Plan!;
        await s.GetRequiredService<PlanService>().ApproveAsync(athlete.Id, plan.Id, default);

        var changes = plan.Workouts.Where(w => w.Type == WorkoutType.Easy).Take(2)
            .Select(w => new MappedChange(w, PlanAdjuster.State(w) with { Km = w.TargetDistanceKm - 1 }, "test", NeedsApproval: true)).ToList();
        var recommendations = s.GetRequiredService<RecommendationService>();
        var rec = Assert.Single(await recommendations.CreateAsync(athlete, plan, changes, "test", "s", null, default));
        Assert.Equal(RecommendationStatus.Proposed, rec.Status);

        time.Advance(TimeSpan.FromHours(49));
        Assert.Single(await recommendations.ExpireDueAsync(default));
        Assert.Equal(DecisionOutcome.AlreadyDecided, (await recommendations.ApproveAsync(athlete, rec.Id, default)).Outcome);
        Assert.Equal(0, await scope.Db().PlanChanges.CountAsync());
    }

    [Fact]
    public async Task A_short_history_still_gets_a_plan_and_the_coach_sees_it()
    {
        var time = new FakeTimeProvider(Start);
        var llm = new ScriptedLlm();
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, new CapturingNotifier(), "sparse_history", llm);
        await using var scope = sp.CreateAsyncScope();
        var s = scope.ServiceProvider;
        var athletes = s.GetRequiredService<AthleteService>();
        var athlete = await athletes.GetOrCreatePrimaryAsync(default);
        await athletes.SetGoalAsync(athlete.Id, new DateOnly(2027, 3, 7), null, "M", 42.195, default);
        await s.GetRequiredService<SyncService>().SyncAsync(athlete, 56, 35, default);

        var plan = (await s.GetRequiredService<PlanService>().GenerateAsync(athlete, default)).Plan!;

        var snapshot = PlanSnapshot.Of(plan)!;
        Assert.True(snapshot.WeeksOfHistory < 4);
        Assert.Null(snapshot.Vo2Max);
        var fitness = ScriptedLlm.Context(llm.Requests.Single(r => r.Purpose == "athlete_assessment"))["fitness"]!;
        Assert.Equal(snapshot.WeeksOfHistory, fitness["weeks_of_history"]!.GetValue<int>());
    }
}
