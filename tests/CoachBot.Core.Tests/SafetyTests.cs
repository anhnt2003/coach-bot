using CoachBot.Core.Abstractions;
using CoachBot.Core.Analysis;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Safety;

namespace CoachBot.Core.Tests;

public class ReadinessEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly Guid AthleteId = Guid.NewGuid();

    private static List<DailyMetric> Baseline(int days = 28, double hrv = 60, int rhr = 50, int sleep = 450, int recovery = 80) =>
        Enumerable.Range(1, days)
            .Select(i => new DailyMetric { AthleteId = AthleteId, Date = Today.AddDays(-i), HrvMs = hrv, RestingHr = rhr, SleepMinutes = sleep, RecoveryPct = recovery, LoadRatio = 1.0 })
            .ToList();

    private static ReadinessAssessment Evaluate(List<DailyMetric> metrics, IReadOnlyList<FeedbackPoint>? feedback = null) =>
        ReadinessEvaluator.Evaluate(new(Today, metrics, feedback ?? [], []));

    [Fact]
    public void Stable_metrics_are_green_with_high_confidence()
    {
        var metrics = Baseline();
        metrics.Add(new DailyMetric { Date = Today, HrvMs = 61, RestingHr = 50, SleepMinutes = 460, RecoveryPct = 85, LoadRatio = 1.0 });

        var result = Evaluate(metrics);

        Assert.Equal(ReadinessLevel.Green, result.Level);
        Assert.Equal(Confidence.High, result.Confidence);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void Hrv_well_below_baseline_is_red()
    {
        var metrics = Baseline();
        // Last 7 days average ~44 vs 60 baseline (< 80%).
        foreach (var m in metrics.Where(m => m.Date > Today.AddDays(-7)))
        {
            m.HrvMs = 44;
        }

        metrics.Add(new DailyMetric { Date = Today, HrvMs = 44, RestingHr = 50, RecoveryPct = 80 });

        var result = Evaluate(metrics);

        Assert.Equal(ReadinessLevel.Red, result.Signals.Single(s => s.Name == "hrv").Level);
        Assert.Equal(ReadinessLevel.Red, result.Level);
    }

    [Fact]
    public void Resting_hr_elevated_two_days_is_red_one_day_is_amber()
    {
        var oneDay = Baseline();
        oneDay.Add(new DailyMetric { Date = Today, RestingHr = 58 });
        Assert.Equal(ReadinessLevel.Amber, Evaluate(oneDay).Signals.Single(s => s.Name == "resting_hr").Level);

        var twoDays = Baseline();
        twoDays.Single(m => m.Date == Today.AddDays(-1)).RestingHr = 59;
        twoDays.Add(new DailyMetric { Date = Today, RestingHr = 59 });
        Assert.Equal(ReadinessLevel.Red, Evaluate(twoDays).Signals.Single(s => s.Name == "resting_hr").Level);
    }

    [Fact]
    public void Three_amber_signals_escalate_to_red()
    {
        var metrics = Baseline();
        metrics.Single(m => m.Date == Today.AddDays(-1)).SleepMinutes = 300;
        metrics.Add(new DailyMetric { Date = Today, RestingHr = 56, SleepMinutes = 320, RecoveryPct = 40 });

        var result = Evaluate(metrics);

        Assert.True(result.Concerns.Count(s => s.Level == ReadinessLevel.Amber) >= 3);
        Assert.Equal(ReadinessLevel.Red, result.Level);
    }

    [Fact]
    public void Missing_data_is_reported_and_lowers_confidence_never_counts_as_good()
    {
        var result = Evaluate([]);

        Assert.Equal(Confidence.Low, result.Confidence);
        Assert.Contains("hrv", result.Missing);
        Assert.Contains("resting_hr", result.Missing);
        Assert.Contains("sleep", result.Missing);
        Assert.Empty(result.Signals);
    }

    [Fact]
    public void Baseline_needs_minimum_points()
    {
        var metrics = Baseline(days: 5);
        metrics.Add(new DailyMetric { Date = Today, HrvMs = 30, RestingHr = 70 });

        var result = Evaluate(metrics);

        Assert.Contains("hrv", result.Missing);
        Assert.Contains("resting_hr", result.Missing);
    }

    [Fact]
    public void Hard_easy_run_and_soreness_are_amber()
    {
        var feedback = new[] { new FeedbackPoint(Today, 8, 7, WorkoutType.Easy) };
        var result = Evaluate([], feedback);

        Assert.Equal(ReadinessLevel.Amber, result.Signals.Single(s => s.Name == "rpe").Level);
        Assert.Equal(ReadinessLevel.Amber, result.Signals.Single(s => s.Name == "soreness").Level);
    }

    [Fact]
    public void Load_ratio_falls_back_to_run_history()
    {
        var runs = Enumerable.Range(0, 28).Where(i => i % 2 == 0)
            .Select(i => new RunLoadPoint(Today.AddDays(-i), i < 7 ? 16 : 8, null))
            .ToList();

        var ratio = ReadinessEvaluator.ComputeAcuteChronicRatio(Today, runs);

        Assert.NotNull(ratio);
        Assert.True(ratio > 1.5);
        var result = ReadinessEvaluator.Evaluate(new(Today, [], [], runs));
        Assert.Equal(ReadinessLevel.Red, result.Signals.Single(s => s.Name == "load_ratio").Level);
    }

    [Fact]
    public void Load_ratio_needs_three_weeks_of_history()
    {
        var runs = new[] { new RunLoadPoint(Today.AddDays(-3), 10, null), new RunLoadPoint(Today, 10, null) };
        Assert.Null(ReadinessEvaluator.ComputeAcuteChronicRatio(Today, runs));
    }
}

public class ChangeClassifierTests
{
    private static readonly DateOnly Today = new(2026, 10, 21); // Wednesday
    private readonly ChangeClassifier _classifier = new(new SafetyOptions());

    private static PlannedWorkout Workout(WorkoutType type, double km, DateOnly date, PlanPhase phase = PlanPhase.Build) =>
        new() { Type = type, TargetDistanceKm = km, Date = date, Phase = phase };

    private ChangeMagnitude Classify(PlannedWorkout w, ProposedChange c) =>
        _classifier.Classify([c], Today, new Dictionary<Guid, PlannedWorkout> { [w.Id] = w });

    [Fact]
    public void Single_small_downgrade_soon_is_minor()
    {
        var w = Workout(WorkoutType.Tempo, 10, Today.AddDays(1));
        Assert.Equal(ChangeMagnitude.Minor, Classify(w, new(w.Id, w.Date, ChangeAction.Downgrade, WorkoutType.Tempo, WorkoutType.Easy, 10, 8)));
    }

    [Fact]
    public void Any_increase_is_major()
    {
        var w = Workout(WorkoutType.Easy, 8, Today.AddDays(1));
        Assert.Equal(ChangeMagnitude.Major, Classify(w, new(w.Id, w.Date, ChangeAction.IncreaseDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 9)));
        Assert.Equal(ChangeMagnitude.Major, Classify(w, new(w.Id, w.Date, ChangeAction.Downgrade, WorkoutType.Easy, WorkoutType.Tempo, 8, 8)));
    }

    [Fact]
    public void Large_reductions_and_long_run_cuts_over_20_percent_are_major()
    {
        var easy = Workout(WorkoutType.Easy, 10, Today.AddDays(1));
        Assert.Equal(ChangeMagnitude.Major, Classify(easy, new(easy.Id, easy.Date, ChangeAction.ReduceDistance, WorkoutType.Easy, WorkoutType.Easy, 10, 6)));

        var longRun = Workout(WorkoutType.Long, 20, Today.AddDays(2));
        Assert.Equal(ChangeMagnitude.Minor, Classify(longRun, new(longRun.Id, longRun.Date, ChangeAction.ReduceDistance, WorkoutType.Long, WorkoutType.Long, 20, 16)));
        Assert.Equal(ChangeMagnitude.Major, Classify(longRun, new(longRun.Id, longRun.Date, ChangeAction.ReduceDistance, WorkoutType.Long, WorkoutType.Long, 20, 15)));
    }

    [Fact]
    public void Dropping_an_easy_run_is_minor_dropping_a_key_session_is_major()
    {
        var easy = Workout(WorkoutType.Easy, 8, Today);
        Assert.Equal(ChangeMagnitude.Minor, Classify(easy, new(easy.Id, easy.Date, ChangeAction.ConvertToRest, WorkoutType.Easy, WorkoutType.Rest, 8, 0)));

        var longRun = Workout(WorkoutType.Long, 20, Today);
        Assert.Equal(ChangeMagnitude.Major, Classify(longRun, new(longRun.Id, longRun.Date, ChangeAction.ConvertToRest, WorkoutType.Long, WorkoutType.Rest, 20, 0)));
        // A "reduction" to rest drops the session just the same.
        Assert.Equal(ChangeMagnitude.Major, Classify(longRun, new(longRun.Id, longRun.Date, ChangeAction.ReduceDistance, WorkoutType.Long, WorkoutType.Rest, 20, 17)));
    }

    [Fact]
    public void Taper_changes_far_future_changes_and_multiple_changes_are_major()
    {
        var taper = Workout(WorkoutType.Easy, 8, Today.AddDays(1), PlanPhase.Taper);
        Assert.Equal(ChangeMagnitude.Major, Classify(taper, new(taper.Id, taper.Date, ChangeAction.ReduceDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 7)));

        var far = Workout(WorkoutType.Easy, 8, Today.AddDays(5));
        Assert.Equal(ChangeMagnitude.Major, Classify(far, new(far.Id, far.Date, ChangeAction.ReduceDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 7)));

        var a = Workout(WorkoutType.Easy, 8, Today);
        var b = Workout(WorkoutType.Easy, 8, Today.AddDays(1));
        var result = _classifier.Classify(
            [new(a.Id, a.Date, ChangeAction.ReduceDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 7), new(b.Id, b.Date, ChangeAction.ReduceDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 7)],
            Today, new Dictionary<Guid, PlannedWorkout> { [a.Id] = a, [b.Id] = b });
        Assert.Equal(ChangeMagnitude.Major, result);
    }

    [Fact]
    public void Swap_within_week_is_minor_across_weeks_is_major()
    {
        var w = Workout(WorkoutType.Tempo, 10, Today.AddDays(1)); // Thursday
        Assert.Equal(ChangeMagnitude.Minor, Classify(w, new(w.Id, w.Date, ChangeAction.SwapDays, WorkoutType.Tempo, WorkoutType.Tempo, 10, 10, NewDate: Today.AddDays(3))));
        Assert.Equal(ChangeMagnitude.Major, Classify(w, new(w.Id, w.Date, ChangeAction.SwapDays, WorkoutType.Tempo, WorkoutType.Tempo, 10, 10, NewDate: Today.AddDays(6))));
    }

    [Fact]
    public void Pace_changes_are_major()
    {
        var w = Workout(WorkoutType.Tempo, 10, Today.AddDays(1));
        Assert.Equal(ChangeMagnitude.Major, Classify(w, new(w.Id, w.Date, ChangeAction.ChangePace, WorkoutType.Tempo, WorkoutType.Tempo, 10, 10, null, 290, 300)));
    }
}

public class SymptomDetectorTests
{
    [Theory]
    [InlineData("Had some chest pain at km 8", SymptomCategory.Cardiac)]
    [InlineData("felt dizzy and nearly fainted", SymptomCategory.Cardiac)]
    [InlineData("Hôm nay bị đau ngực khi chạy", SymptomCategory.Cardiac)]
    [InlineData("hom nay bi dau nguc", SymptomCategory.Cardiac)]
    [InlineData("tim đập nhanh bất thường", SymptomCategory.Cardiac)]
    [InlineData("sharp pain in my shin, limping now", SymptomCategory.Injury)]
    [InlineData("chân bị sưng và đi khập khiễng", SymptomCategory.Injury)]
    [InlineData("I have a fever", SymptomCategory.Illness)]
    [InlineData("bị sốt từ tối qua", SymptomCategory.Illness)]
    [InlineData("toi bi sot va buon non", SymptomCategory.Illness)]
    [InlineData("dau nhoi o dau goi", SymptomCategory.Injury)]
    [InlineData("hom qua bi ngat xiu khi chay", SymptomCategory.Cardiac)]
    public void Detects_red_flags_in_english_and_vietnamese(string text, SymptomCategory expected)
    {
        var result = SymptomDetector.Detect(text);
        Assert.Equal(expected, result.Category);
        Assert.True(result.IsRedFlag);
    }

    [Theory]
    [InlineData("Legs felt heavy but fine overall")]
    [InlineData("Chạy rất tốt, cảm thấy khỏe")]
    [InlineData("chay cham mot xiu, thay sung suc")]
    [InlineData("influence of the heat")]
    [InlineData("")]
    [InlineData(null)]
    public void Normal_feedback_is_not_flagged(string? text)
    {
        Assert.False(SymptomDetector.Detect(text).IsRedFlag);
    }

    [Fact]
    public void Cardiac_outranks_other_categories()
    {
        Assert.Equal(SymptomCategory.Cardiac, SymptomDetector.Detect("fever and chest pain").Category);
    }
}

public class AdaptationEngineTests
{
    private static readonly DateOnly Today = new(2026, 10, 21); // Wednesday

    private static List<PlannedWorkout> Week() =>
    [
        new() { Date = Today, Type = WorkoutType.Easy, TargetDistanceKm = 8, WeekIndex = 3 },
        new() { Date = Today.AddDays(1), Type = WorkoutType.Tempo, TargetDistanceKm = 10, WeekIndex = 3 },
        new() { Date = Today.AddDays(3), Type = WorkoutType.Easy, TargetDistanceKm = 8, WeekIndex = 3 },
        new() { Date = Today.AddDays(4), Type = WorkoutType.Long, TargetDistanceKm = 22, WeekIndex = 3 },
    ];

    private static ReadinessAssessment Level(ReadinessLevel level) => new(level, Confidence.High, [new("hrv", level, 40, 60)], []);

    private static AdaptationProposal Propose(ReadinessLevel today, params ReadinessLevel[] previous) =>
        AdaptationEngine.Propose(new(Today, Level(today), previous, Week(), null, null));

    [Fact]
    public void Green_produces_no_change()
    {
        Assert.True(Propose(ReadinessLevel.Green, ReadinessLevel.Green).IsEmpty);
    }

    [Fact]
    public void Amber_softens_next_hard_session_only()
    {
        var p = Propose(ReadinessLevel.Amber, ReadinessLevel.Green);
        var change = Assert.Single(p.Changes);
        Assert.Equal(WorkoutType.Tempo, change.FromType);
        Assert.Equal(WorkoutType.Easy, change.ToType);
        Assert.Equal(8, change.ToKm);
    }

    [Fact]
    public void Red_turns_todays_easy_run_into_rest()
    {
        var p = Propose(ReadinessLevel.Red, ReadinessLevel.Green);
        var change = Assert.Single(p.Changes);
        Assert.Equal(ChangeAction.ConvertToRest, change.Action);
        Assert.Equal("adapt.red_day", p.ReasonKey);
    }

    [Fact]
    public void Amber_two_days_running_eases_the_next_session_like_a_red_day()
    {
        var p = Propose(ReadinessLevel.Amber, ReadinessLevel.Amber, ReadinessLevel.Green);
        var change = Assert.Single(p.Changes);
        Assert.Equal(ChangeAction.ConvertToRest, change.Action);
        Assert.Equal("adapt.red_day", p.ReasonKey);

        // A deload still needs red two days running (or red after two poor days).
        Assert.Equal("adapt.red_day", Propose(ReadinessLevel.Red, ReadinessLevel.Amber, ReadinessLevel.Green).ReasonKey);
        Assert.Equal("adapt.deload", Propose(ReadinessLevel.Red, ReadinessLevel.Amber, ReadinessLevel.Amber).ReasonKey);
    }

    [Fact]
    public void Two_red_days_trigger_a_deload_of_the_week()
    {
        var p = Propose(ReadinessLevel.Red, ReadinessLevel.Red);
        Assert.Equal("adapt.deload", p.ReasonKey);
        Assert.Equal(4, p.Changes.Count);
        Assert.All(p.Changes, c => Assert.True(c.ToKm <= c.FromKm * 0.7 + 0.01));
        Assert.DoesNotContain(p.Changes, c => c.ToType.IsQuality());
    }

    [Fact]
    public void Fatigue_never_increases_load()
    {
        foreach (var level in new[] { ReadinessLevel.Amber, ReadinessLevel.Red })
        foreach (var prev in new[] { ReadinessLevel.Green, ReadinessLevel.Amber, ReadinessLevel.Red })
        {
            var p = Propose(level, prev, prev);
            Assert.All(p.Changes, c =>
            {
                Assert.True(c.ToKm <= c.FromKm);
                Assert.True(c.ToType.IntensityRank() <= c.FromType.IntensityRank());
            });
        }
    }

    [Fact]
    public void Fitness_gain_proposes_pace_change_which_is_major()
    {
        var week = Week();
        week[1].PaceMinSecPerKm = 300;
        week[1].PaceMaxSecPerKm = 315;
        var p = AdaptationEngine.Propose(new(Today, Level(ReadinessLevel.Green), [], week, 285, 300));

        var change = Assert.Single(p.Changes);
        Assert.Equal(ChangeAction.ChangePace, change.Action);
        Assert.Equal(285, change.NewPaceMinSecPerKm);
        var magnitude = new ChangeClassifier(new SafetyOptions()).Classify(p.Changes, Today, week.ToDictionary(w => w.Id));
        Assert.Equal(ChangeMagnitude.Major, magnitude);
    }

    private static ReadinessAssessment Ready(ReadinessLevel level, Confidence confidence = Confidence.High) => new(level, confidence, [], []);

    [Fact]
    public void Llm_proposals_are_rebuilt_from_the_database_and_unknown_or_rule_covered_workouts_are_dropped()
    {
        var week = Week();
        var byId = week.ToDictionary(w => w.Id);
        var rule = new ProposedChange(week[1].Id, week[1].Date, ChangeAction.Downgrade, WorkoutType.Tempo, WorkoutType.Easy, 10, 8);
        var llm = new List<ProposedChange>
        {
            new(week[1].Id, week[1].Date, ChangeAction.ConvertToRest, WorkoutType.Tempo, WorkoutType.Rest, 10, 0), // rules already cover it
            new(Guid.NewGuid(), Today, ChangeAction.ReduceDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 6),      // unknown workout
            new(week[3].Id, week[3].Date, ChangeAction.ReduceDistance, WorkoutType.Easy, WorkoutType.Easy, 99, 18), // lies about "before"
            new(week[2].Id, week[2].Date, ChangeAction.ChangePace, WorkoutType.Easy, WorkoutType.Easy, 8, 8, null, 200, 210),
        };

        var (easing, progression) = AdaptationEngine.MergeLlmProposals([rule], llm, byId, Today, Ready(ReadinessLevel.Green));

        Assert.Equal(2, easing.Count);
        Assert.Same(rule, easing[0]);
        var accepted = easing[1];
        Assert.Equal(week[3].Id, accepted.WorkoutId);
        Assert.Equal(22, accepted.FromKm);
        Assert.Equal(WorkoutType.Long, accepted.FromType);
        Assert.Empty(progression);
    }

    [Fact]
    public void Llm_increases_are_only_accepted_on_green_days_with_enough_confidence()
    {
        var week = Week();
        var byId = week.ToDictionary(w => w.Id);
        var increase = new ProposedChange(week[2].Id, week[2].Date, ChangeAction.IncreaseDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 10);
        var upgrade = new ProposedChange(week[0].Id, week[0].Date, ChangeAction.Upgrade, WorkoutType.Easy, WorkoutType.Tempo, 8, 8);

        Assert.Equal(2, AdaptationEngine.MergeLlmProposals([], [increase, upgrade], byId, Today, Ready(ReadinessLevel.Green)).Progression.Count);
        Assert.Empty(AdaptationEngine.MergeLlmProposals([], [increase, upgrade], byId, Today, Ready(ReadinessLevel.Amber)).Progression);
        Assert.Empty(AdaptationEngine.MergeLlmProposals([], [increase, upgrade], byId, Today, Ready(ReadinessLevel.Green, Confidence.Low)).Progression);
        Assert.Empty(AdaptationEngine.MergeLlmProposals([], [increase, upgrade], byId, Today, null).Progression);
    }

    [Fact]
    public void Llm_reduction_to_rest_is_dropped()
    {
        var week = Week();
        var toRest = new ProposedChange(week[3].Id, week[3].Date, ChangeAction.ReduceDistance, WorkoutType.Long, WorkoutType.Rest, 22, 18);
        Assert.Empty(AdaptationEngine.MergeLlmProposals([], [toRest], week.ToDictionary(w => w.Id), Today, Ready(ReadinessLevel.Green)).Easing);
    }

    [Theory]
    [InlineData(true, PlanPhase.Build)]
    [InlineData(false, PlanPhase.Taper)]
    public void Llm_progression_never_targets_cutback_or_taper_weeks(bool cutback, PlanPhase phase)
    {
        var week = Week();
        week.ForEach(w => { w.IsCutbackWeek = cutback; w.Phase = phase; });
        var increase = new ProposedChange(week[2].Id, week[2].Date, ChangeAction.IncreaseDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 10);
        var upgrade = new ProposedChange(week[0].Id, week[0].Date, ChangeAction.Upgrade, WorkoutType.Easy, WorkoutType.Tempo, 8, 8);

        Assert.Empty(AdaptationEngine.MergeLlmProposals([], [increase, upgrade], week.ToDictionary(w => w.Id), Today, Ready(ReadinessLevel.Green)).Progression);
    }

    [Fact]
    public void Llm_session_increase_is_capped_and_swaps_go_to_easing()
    {
        var week = Week();
        var byId = week.ToDictionary(w => w.Id);
        var tooBig = new ProposedChange(week[2].Id, week[2].Date, ChangeAction.IncreaseDistance, WorkoutType.Easy, WorkoutType.Easy, 8, 14);
        var swap = new ProposedChange(week[1].Id, week[1].Date, ChangeAction.SwapDays, WorkoutType.Tempo, WorkoutType.Tempo, 10, 10, Today.AddDays(2));

        var (easing, progression) = AdaptationEngine.MergeLlmProposals([], [tooBig, swap], byId, Today, Ready(ReadinessLevel.Green));

        Assert.Empty(progression);
        Assert.Equal(ChangeAction.SwapDays, Assert.Single(easing).Action);
    }
}

public class PaceZoneCalculatorTests
{
    [Fact]
    public void Prefers_coros_threshold()
    {
        var r = PaceZoneCalculator.Calculate(new(300, 12000, [new(10, 3000)], null, 42.195, 190, null, 2026));
        Assert.Equal(ZoneBasis.CorosThreshold, r.Zones.Basis);
        Assert.Equal(300, r.Zones.ThresholdPaceSecPerKm);
    }

    [Fact]
    public void Falls_back_to_runs_then_heart_rate_then_rpe()
    {
        var fromRuns = PaceZoneCalculator.Calculate(new(null, null, [new(10, 3300)], null, 42.195, null, null, 2026));
        Assert.Equal(ZoneBasis.RecentRuns, fromRuns.Zones.Basis);
        Assert.Contains("zones.estimated_from_runs", fromRuns.Warnings);

        var fromHr = PaceZoneCalculator.Calculate(new(null, null, [], null, 42.195, null, 1990, 2026));
        Assert.Equal(ZoneBasis.HeartRate, fromHr.Zones.Basis);
        Assert.Contains("zones.max_hr_estimated", fromHr.Warnings);

        var rpe = PaceZoneCalculator.Calculate(new(null, null, [], null, 42.195, null, null, 2026));
        Assert.Equal(ZoneBasis.Rpe, rpe.Zones.Basis);
        Assert.Contains("zones.rpe_only", rpe.Warnings);
    }

    [Fact]
    public void Unrealistic_goal_is_not_used_for_training_paces()
    {
        // Threshold 5:30/km → predicted MP ≈ 5:53; a 3:00 marathon (4:16/km) is far beyond that.
        var r = PaceZoneCalculator.Calculate(new(330, null, [], 3 * 3600, 42.195, null, null, 2026));
        Assert.Contains("zones.goal_too_ambitious", r.Warnings);
        Assert.Equal((int)Math.Round(330 * 1.07), r.Zones.MarathonPaceSecPerKm);
    }

    [Fact]
    public void Realistic_goal_sets_marathon_pace()
    {
        var r = PaceZoneCalculator.Calculate(new(330, null, [], 4 * 3600 + 10 * 60, 42.195, null, null, 2026));
        Assert.Equal((int)Math.Round((4 * 3600 + 10 * 60) / 42.195), r.Zones.MarathonPaceSecPerKm);
    }
}

public class RunAnalyzerTests
{
    [Fact]
    public void Flags_easy_run_too_fast_and_hr_above_zone()
    {
        var planned = new PlannedWorkout { Type = WorkoutType.Easy, TargetDistanceKm = 10, PaceMinSecPerKm = 380, PaceMaxSecPerKm = 420, HrMin = 120, HrMax = 145 };
        var run = new Activity { DistanceMeters = 10000, AvgPaceSecPerKm = 330, AvgHr = 162 };

        var result = RunAnalyzer.Analyze(run, planned);

        Assert.Equal(ComplianceStatus.Faster, result.PaceStatus);
        Assert.Equal(ComplianceStatus.Above, result.HrStatus);
        Assert.Contains("easy_too_fast", result.Flags);
        Assert.Contains("hr_above_zone", result.Flags);
    }

    [Fact]
    public void Interval_average_pace_is_not_judged()
    {
        var planned = new PlannedWorkout { Type = WorkoutType.Interval, TargetDistanceKm = 10, PaceMinSecPerKm = 270, PaceMaxSecPerKm = 285 };
        var run = new Activity { DistanceMeters = 10000, AvgPaceSecPerKm = 340 };

        Assert.Equal(ComplianceStatus.Unknown, RunAnalyzer.Analyze(run, planned).PaceStatus);
    }

    [Fact]
    public void Matches_same_day_before_adjacent_day()
    {
        var day = new DateOnly(2026, 10, 21);
        var yesterday = new PlannedWorkout { Date = day.AddDays(-1), Type = WorkoutType.Easy, TargetDistanceKm = 10 };
        var today = new PlannedWorkout { Date = day, Type = WorkoutType.Tempo, TargetDistanceKm = 12 };

        Assert.Same(today, WorkoutMatcher.Match(day, 10, [yesterday, today]));
        Assert.Null(WorkoutMatcher.Match(day.AddDays(5), 10, [yesterday, today]));
    }
}
