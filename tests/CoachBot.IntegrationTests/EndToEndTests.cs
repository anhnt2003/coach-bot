using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Safety;
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

            // Deleting the athlete removes all their data (cascade), including plans that reference goals.
            db.Athletes.Remove(a);
            await db.SaveChangesAsync();
            Assert.Equal(0, await db.Activities.CountAsync());
            Assert.Equal(0, await db.TrainingPlans.CountAsync());
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
    public async Task Full_coaching_loop_with_fatigue_approval_symptom_pause_and_resume()
    {
        var time = new FakeTimeProvider(Start);
        var notifier = new CapturingNotifier();
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, notifier, scenario: "fatigued");
        Guid athleteId;
        Guid planId;

        // 1. Onboarding: goal, history backfill, plan proposal, approval, push to (fake) COROS.
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athletes = s.GetRequiredService<AthleteService>();
            var athlete = await athletes.LinkTelegramAsync(42, "Anh", default);
            athleteId = athlete.Id;
            await athletes.SetGoalAsync(athlete.Id, new DateOnly(2027, 3, 7), 4 * 3600, "Hanoi Marathon", 42.195, default);
            Assert.True((await s.GetRequiredService<SyncService>().SyncAsync(athlete, 56, 35, default)).Success);

            var plans = s.GetRequiredService<PlanService>();
            var result = await plans.GenerateAsync(athlete, default);
            Assert.Null(result.FailureKey);
            var plan = result.Plan!;
            planId = plan.Id;
            Assert.Equal(PlanStatus.PendingApproval, plan.Status);
            Assert.Equal(ZoneBasis.CorosThreshold, CoachJson.Deserialize<PlanSnapshot>(plan.InputsJson)!.Zones.Basis);
            Assert.All(plan.Workouts, w => Assert.False(string.IsNullOrEmpty(w.Title)));

            var snapshot = CoachJson.Deserialize<PlanSnapshot>(plan.InputsJson)!;
            Assert.Empty(new PlanValidator(new SafetyOptions()).ValidatePlan(plan.Workouts, snapshot.Baseline, snapshot.Zones));

            var proposal = plans.RenderProposal(athlete, plan, (await athletes.GetActiveGoalAsync(athlete.Id, default))!, result.Warnings);
            Assert.Contains($"plan:approve:{plan.Id}", proposal.Buttons!.SelectMany(r => r).Select(b => b.CallbackData));

            Assert.NotNull((await plans.ApproveAsync(athlete.Id, plan.Id, default)).Plan);
            var push = await s.GetRequiredService<CorosPushService>().PushUpcomingAsync(athlete, default);
            Assert.True(push.Pushed >= 1);
            Assert.Equal(push.Pushed, FakeCorosGateway.PushedWorkouts.Count);
        }

        // 2. Tuesday of week 1, after the morning run: fatigue for three days → deload proposal (major, needs approval).
        time.SetUtcNow(new DateTimeOffset(2026, 10, 27, 5, 0, 0, TimeSpan.Zero));
        Recommendation rec;
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athlete = await scope.Db().Athletes.SingleAsync();
            var sync = await s.GetRequiredService<SyncService>().SyncAsync(athlete, 3, 7, default);
            // Sat + Sun (before the plan started) and today's run. Older runs are recorded but never trigger adaptations.
            Assert.Equal(3, sync.NewRunIds.Count);
            var messagesBefore = notifier.Messages.Count;
            await s.GetRequiredService<AnalysisService>().AnalyzeRunsAsync(athlete, sync.NewRunIds, default);
            Assert.Equal(messagesBefore + 1, notifier.Messages.Count);

            var analysis = await scope.Db().RunAnalyses.SingleAsync(r => r.PlannedWorkoutId != null);
            Assert.Equal(ReadinessLevel.Red, analysis.Readiness);
            Assert.NotNull(analysis.PlannedWorkoutId);
            Assert.Equal(WorkoutStatus.Completed, (await scope.Db().PlannedWorkouts.SingleAsync(w => w.Id == analysis.PlannedWorkoutId)).Status);

            rec = await scope.Db().Recommendations.SingleAsync();
            Assert.Equal(ChangeMagnitude.Major, rec.Magnitude);
            Assert.Equal(RecommendationStatus.Proposed, rec.Status);
            Assert.Equal("adapt.deload", rec.Trigger);
            Assert.Equal(0, await scope.Db().PlanChanges.CountAsync()); // nothing applied before approval

            var message = notifier.Messages.Last();
            Assert.Contains("🔴", message.Text, StringComparison.Ordinal);
            var buttons = message.Buttons!.SelectMany(r => r).Select(b => b.CallbackData).ToList();
            Assert.Contains($"rec:approve:{rec.Id}", buttons);
            Assert.Contains(buttons, b => b.StartsWith("rpe:", StringComparison.Ordinal));
            Assert.Single(await scope.Db().Alerts.Where(a => a.Type == AlertType.Fatigue).ToListAsync());
        }

        // 3. Athlete approves: changes applied with audit trail; already-pushed workouts are marked for update.
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athlete = await scope.Db().Athletes.SingleAsync();
            var before = await scope.Db().PlannedWorkouts.AsNoTracking().Where(w => w.PlanId == planId).ToDictionaryAsync(w => w.Id);
            var (outcome, _) = await s.GetRequiredService<RecommendationService>().ApproveAsync(athlete, rec.Id, default);
            Assert.Equal(DecisionOutcome.Done, outcome);

            var changes = await scope.Db().PlanChanges.Where(c => c.RecommendationId == rec.Id).ToListAsync();
            Assert.NotEmpty(changes);
            foreach (var change in changes)
            {
                var after = await scope.Db().PlannedWorkouts.SingleAsync(w => w.Id == change.PlannedWorkoutId);
                Assert.True(after.TargetDistanceKm < before[after.Id].TargetDistanceKm, "fatigue changes only reduce load");
                Assert.False(after.Type.IsQuality());
            }

            var push = await s.GetRequiredService<CorosPushService>().PushUpcomingAsync(athlete, default);
            Assert.True(push.Updated >= 1);
        }

        // 4. A minor change is auto-applied and can be undone from the audit trail.
        await using (var scope = sp.CreateAsyncScope())
        {
            var s = scope.ServiceProvider;
            var athlete = await scope.Db().Athletes.SingleAsync();
            var plan = await scope.Db().TrainingPlans.Include(p => p.Workouts).SingleAsync(p => p.Id == planId);
            var target = plan.Workouts.Where(w => w.Status == WorkoutStatus.Planned && w.Type == WorkoutType.Easy && w.Date > new DateOnly(2026, 11, 2))
                .OrderBy(w => w.Date).First();
            var today = target.Date.AddDays(-1);
            var change = new ProposedChange(target.Id, target.Date, ChangeAction.ReduceDistance, target.Type, target.Type, target.TargetDistanceKm,
                Math.Floor(target.TargetDistanceKm * 0.8 * 2) / 2);
            var recommendations = s.GetRequiredService<RecommendationService>();

            var minor = Assert.Single(await recommendations.CreateAsync(athlete, plan, [change], [], null, "test", "s", null, today, default));
            Assert.Equal(RecommendationStatus.AutoApplied, minor.Status);
            Assert.Equal(change.ToKm, target.TargetDistanceKm);

            Assert.Equal(DecisionOutcome.Done, await recommendations.UndoAsync(athlete, minor.Id, default));
            var restored = await scope.Db().PlannedWorkouts.AsNoTracking().SingleAsync(w => w.Id == target.Id);
            Assert.Equal(change.FromKm, restored.TargetDistanceKm);
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
    public async Task Unanswered_major_proposals_expire_and_leave_the_plan_unchanged()
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

        var targets = plan.Workouts.Where(w => w.Type == WorkoutType.Easy).Take(2).ToList();
        var changes = targets.Select(w => new ProposedChange(w.Id, w.Date, ChangeAction.ReduceDistance, w.Type, w.Type, w.TargetDistanceKm, w.TargetDistanceKm - 1)).ToList();
        var recommendations = s.GetRequiredService<RecommendationService>();
        var rec = Assert.Single(await recommendations.CreateAsync(athlete, plan, changes, [], null, "test", "s", null, new DateOnly(2026, 10, 25), default));
        Assert.Equal(RecommendationStatus.Proposed, rec.Status);

        time.Advance(TimeSpan.FromHours(49));
        Assert.Single(await recommendations.ExpireDueAsync(default));
        Assert.Equal(DecisionOutcome.AlreadyDecided, (await recommendations.ApproveAsync(athlete, rec.Id, default)).Outcome);
        Assert.Equal(0, await scope.Db().PlanChanges.CountAsync());
    }

    [Theory]
    [InlineData("missing_hrv")]
    [InlineData("sparse_history")]
    public async Task Missing_data_lowers_confidence_and_warns_but_still_works(string scenario)
    {
        var time = new FakeTimeProvider(Start);
        await using var sp = TestHost.Build(await pg.CreateDatabaseAsync(), time, new CapturingNotifier(), scenario);
        await using var scope = sp.CreateAsyncScope();
        var s = scope.ServiceProvider;
        var athletes = s.GetRequiredService<AthleteService>();
        var athlete = await athletes.GetOrCreatePrimaryAsync(default);
        await athletes.SetGoalAsync(athlete.Id, new DateOnly(2027, 3, 7), null, "M", 42.195, default);
        await s.GetRequiredService<SyncService>().SyncAsync(athlete, 56, 35, default);

        var readiness = await s.GetRequiredService<ReadinessService>().EvaluateAsync(athlete, new DateOnly(2026, 10, 21), default);
        var result = await s.GetRequiredService<PlanService>().GenerateAsync(athlete, default);

        if (scenario == "missing_hrv")
        {
            Assert.Contains("hrv", readiness.Today.Missing);
            Assert.Contains("sleep", readiness.Today.Missing);
            Assert.NotEqual(Confidence.High, readiness.Today.Confidence);
            Assert.NotNull(result.Plan);
        }
        else
        {
            Assert.Contains("plan.warn_little_history", result.Warnings);
            Assert.NotEqual(ZoneBasis.CorosThreshold, CoachJson.Deserialize<PlanSnapshot>(result.Plan!.InputsJson)!.Zones.Basis);
        }
    }
}
