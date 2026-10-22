using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Safety;
using CoachBot.Core.Services;

namespace CoachBot.Core.Tests;

/// <summary>Structured workouts designed by the LLM, and the rules that keep them safe.</summary>
public class StructuredWorkoutTests
{
    private static readonly SafetyOptions Options = new();
    private static readonly DateOnly Today = new(2026, 10, 21);
    // Threshold 5:00/km, half-marathon goal.
    private static readonly TrainingZones HalfZones = PaceZoneCalculator.Calculate(new(300, null, [], null, 21.0975, 185, null, 2026)).Zones;

    private static readonly List<WorkoutBlock> FiveByOneK =
    [
        new(1, new(Intensity.Easy, Km: 2)),
        new(5, new(Intensity.FiveK, Km: 1), new(Intensity.Recovery, Minutes: 2)),
        new(1, new(Intensity.Easy, Km: 2)),
    ];

    private static PlannedWorkout Workout(WorkoutType type, double km, IReadOnlyList<WorkoutBlock>? blocks = null, DateOnly? date = null) => new()
    {
        Date = date ?? new DateOnly(2026, 10, 27),
        Type = type,
        TargetDistanceKm = km,
        StepsJson = WorkoutStructure.Serialize(blocks is null ? null : WorkoutStructure.Resolve(blocks, HalfZones)),
        PaceMinSecPerKm = 280,
        PaceMaxSecPerKm = 290,
    };

    [Fact]
    public void Measures_distance_and_time_at_each_intensity()
    {
        var total = WorkoutStructure.TotalKm(FiveByOneK, HalfZones);
        var recoveryKm = 5 * 2 * 60.0 / HalfZones.ConversionPaceFor(Intensity.Recovery);
        Assert.Equal(9 + recoveryKm, total, 3);

        var w = Workout(WorkoutType.Interval, 10, FiveByOneK);
        Assert.Equal(5, WorkoutStructure.HardKm(w, HalfZones), 3);
        Assert.Equal(5, WorkoutStructure.BandKm(w, HalfZones)[IntensityBand.Vo2], 3);

        var steadyFinish = Workout(WorkoutType.Long, 16, [new(1, new(Intensity.Easy, Km: 12)), new(1, new(Intensity.Steady, Km: 4))]);
        Assert.Equal(2, WorkoutStructure.HardKm(steadyFinish, HalfZones), 3);
    }

    [Fact]
    public void Intensity_hidden_in_an_easy_run_is_rejected()
    {
        var problems = WorkoutStructure.Problems(WorkoutType.Easy, FiveByOneK, Intensity.HalfMarathon);
        Assert.Contains(problems, p => p.Code == "steps_intensity_not_allowed");
    }

    [Fact]
    public void Quality_sessions_need_their_main_set_and_strides_stay_short()
    {
        List<WorkoutBlock> easyOnly = [new(1, new(Intensity.Easy, Km: 8))];
        Assert.Contains(WorkoutStructure.Problems(WorkoutType.Interval, easyOnly, Intensity.HalfMarathon), p => p.Code == "steps_missing_main_set");
        Assert.Contains(WorkoutStructure.Problems(WorkoutType.RacePace, [new(1, new(Intensity.Marathon, Km: 8))], Intensity.HalfMarathon),
            p => p.Code == "steps_missing_main_set");

        List<WorkoutBlock> longStrides = [new(1, new(Intensity.Easy, Km: 6)), new(8, new(Intensity.Strides, Minutes: 1), new(Intensity.Recovery, Minutes: 1))];
        Assert.Contains(WorkoutStructure.Problems(WorkoutType.Easy, longStrides, Intensity.HalfMarathon), p => p.Code == "strides_too_long");

        List<WorkoutBlock> fastRecovery = [new(4, new(Intensity.FiveK, Km: 1), new(Intensity.Threshold, Km: 1))];
        Assert.Contains(WorkoutStructure.Problems(WorkoutType.Interval, fastRecovery, Intensity.HalfMarathon), p => p.Code == "steps_invalid");

        List<WorkoutBlock> strides = [new(1, new(Intensity.Easy, Km: 8)), new(6, new(Intensity.Strides, Minutes: 0.33), new(Intensity.Recovery, Minutes: 1))];
        Assert.Empty(WorkoutStructure.Problems(WorkoutType.Easy, strides, Intensity.HalfMarathon));
    }

    [Fact]
    public void Validator_limits_time_at_intensity_per_session()
    {
        // 12 × 1 km at 5K pace in a ~40 km week: 12 km of VO2max work, far above 8% of the week (Daniels).
        List<WorkoutBlock> tooMuch = [new(1, new(Intensity.Easy, Km: 2)), new(12, new(Intensity.FiveK, Km: 1), new(Intensity.Recovery, Km: 0.4)), new(1, new(Intensity.Easy, Km: 2))];
        var monday = new DateOnly(2026, 10, 26);
        List<PlannedWorkout> week =
        [
            Workout(WorkoutType.Interval, 21, tooMuch, monday.AddDays(1)),
            Workout(WorkoutType.Easy, 8, date: monday.AddDays(3)),
            Workout(WorkoutType.Long, 12, date: monday.AddDays(6)),
        ];

        var violations = new PlanValidator(Options.ForRace(21.0975)).ValidatePlan(week, new PlanBaseline(40, 14), HalfZones);

        Assert.Contains(violations, v => v.Code == "too_much_vo2");
    }

    [Fact]
    public void Steps_must_add_up_to_the_session_distance()
    {
        var violations = new PlanValidator(Options).ValidatePlan([Workout(WorkoutType.Interval, 16, FiveByOneK)], new PlanBaseline(60, 20), HalfZones);
        Assert.Contains(violations, v => v.Code == "steps_distance_mismatch");
    }

    [Fact]
    public void Mapper_turns_llm_steps_into_a_workout_with_paces_from_fitness()
    {
        var start = new DateOnly(2026, 10, 26);
        var race = start.AddDays(6);
        var design = new PlanDesign("r",
        [
            new WeekDesign(1, "taper", false, null,
            [
                new SessionDesign("tue", "race_pace", 99, "Hold back", "3 × 2 km at half-marathon pace",
                [
                    new BlockDesign(1, new PartDesign("easy", Km: 2)),
                    new BlockDesign(3, new PartDesign("race_pace", Km: 2), new PartDesign("recovery", Minutes: 2)),
                    new BlockDesign(1, new PartDesign("easy", Km: 2)),
                ]),
            ]),
        ]);

        var (workouts, errors) = PlanDesignMapper.Map(design, start, race, 21.0975, HalfZones);

        Assert.Empty(errors);
        var w = workouts.Single(x => x.Type == WorkoutType.RacePace);
        var blocks = WorkoutStructure.Parse(w.StepsJson)!;
        Assert.Equal(Intensity.HalfMarathon, blocks[1].Work.Intensity);
        Assert.Equal(HalfZones.PaceFor(Intensity.HalfMarathon)!.Fast, blocks[1].Work.PaceFast);
        Assert.Equal(HalfZones.PaceFor(Intensity.HalfMarathon)!.Fast, w.PaceMinSecPerKm);
        Assert.InRange(w.TargetDistanceKm, 11, 12); // from the steps, not the LLM's "99"
        Assert.Equal("3 × 2 km at half-marathon pace", w.Title);
    }

    [Fact]
    public void Unknown_intensity_is_reported_for_repair()
    {
        var start = new DateOnly(2026, 10, 26);
        var design = new PlanDesign("r",
            [new WeekDesign(1, "taper", false, null, [new SessionDesign("tue", "interval", 8, Steps: [new BlockDesign(4, new PartDesign("warp_speed", Km: 1))])])]);

        var (_, errors) = PlanDesignMapper.Map(design, start, start.AddDays(6), 21.0975, HalfZones);

        Assert.Contains(errors, e => e.Code == "design_unknown_intensity");
    }

    [Fact]
    public void Shortening_a_structured_session_trims_warm_up_then_repeats()
    {
        var resized = WorkoutStructure.Resize(FiveByOneK, 7, HalfZones)!;
        Assert.InRange(WorkoutStructure.TotalKm(resized, HalfZones), 6.75, 7.25);
        Assert.True(resized[1].Repeat < 5);
        Assert.True(resized[0].Work.Km >= 1 && resized[2].Work.Km >= 1);

        var longer = WorkoutStructure.Resize(FiveByOneK, 13, HalfZones)!;
        Assert.InRange(WorkoutStructure.TotalKm(longer, HalfZones), 12.75, 13.25);
        Assert.Equal(5, longer[1].Repeat);
    }

    [Fact]
    public void Adjusting_a_structured_workout_keeps_steps_consistent_and_undo_restores_them()
    {
        var w = Workout(WorkoutType.Interval, 10.5, FiveByOneK);
        var original = PlanAdjuster.Snapshot(w);
        List<PlannedWorkout> plan = [w];

        PlanAdjuster.Apply(plan, new ProposedChange(w.Id, w.Date, ChangeAction.ReduceDistance, WorkoutType.Interval, WorkoutType.Interval, 10.5, 8), HalfZones);
        Assert.InRange(WorkoutStructure.TotalKm(WorkoutStructure.Parse(w.StepsJson)!, HalfZones), 7.75, 8.25);

        PlanAdjuster.Apply(plan, new ProposedChange(w.Id, w.Date, ChangeAction.ChangePace, WorkoutType.Interval, WorkoutType.Interval, 8, 8, null, 266, 276), HalfZones);
        var fast = WorkoutStructure.Parse(w.StepsJson)!.First(b => b.Work.Intensity == Intensity.FiveK).Work.PaceFast;
        Assert.Equal((int)Math.Round(HalfZones.PaceFor(Intensity.FiveK)!.Fast * 266 / 280.0), fast);

        PlanAdjuster.Apply(plan, new ProposedChange(w.Id, w.Date, ChangeAction.Downgrade, WorkoutType.Interval, WorkoutType.Easy, 8, 6), HalfZones);
        Assert.Null(w.StepsJson);

        PlanAdjuster.Restore(w, CoachJson.Deserialize<WorkoutState>(original)!);
        Assert.Equal(5, WorkoutStructure.Parse(w.StepsJson)![1].Repeat);
    }

    [Fact]
    public void A_long_run_with_a_fast_finish_is_not_judged_on_average_pace()
    {
        var planned = Workout(WorkoutType.Long, 16, [new(1, new(Intensity.Easy, Km: 12)), new(1, new(Intensity.HalfMarathon, Km: 4))]);
        var run = new Activity { DistanceMeters = 16000, AvgPaceSecPerKm = 300 };

        Assert.Equal(Analysis.ComplianceStatus.Unknown, Analysis.RunAnalyzer.Analyze(run, planned).PaceStatus);
    }
}

public class GoalDistanceTests
{
    [Theory]
    [InlineData("2027-03-07 3:59 Hanoi Marathon", 42.195, 3 * 3600 + 59 * 60, "Hanoi Marathon")]
    [InlineData("2026-12-20 half 1:45", 21.0975, 6300, "Half Marathon")]
    [InlineData("2026-12-20 half marathon 1:45", 21.0975, 6300, "Half Marathon")]
    [InlineData("2027-03-07 Full Marathon 3:59 Hanoi", 42.195, 3 * 3600 + 59 * 60, "Hanoi")]
    [InlineData("2026-12-20 1:45 half VnExpress", 21.0975, 6300, "VnExpress")]
    [InlineData("2026-12-20 1:45 VnExpress Half Marathon", 21.0975, 6300, "VnExpress Half Marathon")]
    [InlineData("2026-12-20 Giải bán marathon Huế", 21.0975, null, "Giải bán marathon Huế")]
    [InlineData("2026-11-29 5k 22:30", 5, 1350, "5K")]
    [InlineData("2026-11-29 10K 0:48 Run", 10, 2880, "Run")]
    [InlineData("2026-11-29 10k 48:00", 10, 2880, "10K")]
    [InlineData("2026-11-29 21.1k", 21.0975, null, "Half Marathon")]
    [InlineData("2026-11-29 15km 1:10:00 City run", 15, 4200, "City run")]
    [InlineData("2026-11-29", 42.195, null, "Marathon")]
    public void Parses_distance_time_and_name(string args, double km, int? seconds, string name)
    {
        Assert.True(GoalParser.TryParse(args, out var goal, out _));
        Assert.Equal(km, goal!.DistanceKm, 3);
        Assert.Equal(seconds, goal.GoalSeconds);
        Assert.Equal(name, goal.Name);
    }

    [Theory]
    [InlineData("next sunday", "goal.usage")]
    [InlineData("2026-11-29 5k 1:30", "goal.invalid_time")]
    [InlineData("2026-11-29 marathon 1:30", "goal.invalid_time")]
    [InlineData("2026-11-29 50k", "goal.invalid_distance")]
    public void Rejects_unusable_goals(string args, string error)
    {
        Assert.False(GoalParser.TryParse(args, out _, out var key));
        Assert.Equal(error, key);
    }

    [Fact]
    public void Race_pace_follows_a_realistic_goal_for_any_distance()
    {
        // Threshold 5:00/km predicts a ~1:48:45 half (5:09/km); a 1:46 goal is realistic.
        var r = PaceZoneCalculator.Calculate(new(300, null, [], 6360, 21.0975, 185, null, 2026));
        Assert.Empty(r.Warnings);
        Assert.Equal((int)Math.Round(6360 / 21.0975), r.Zones.RacePaceSecPerKm);
        Assert.Equal(r.Zones.PaceFor(Intensity.HalfMarathon), r.Zones.PaceFor(WorkoutType.Race));
        Assert.Equal((int)Math.Round(300 * 1.07), r.Zones.MarathonPaceSecPerKm);
        Assert.Equal(new HrRange(154, 165), r.Zones.HrFor(WorkoutType.Race));
    }

    [Fact]
    public void Too_ambitious_goal_trains_at_predicted_pace_for_short_races_too()
    {
        // A 20:00 5K is 4:00/km; threshold 5:00/km predicts ~4:44/km.
        var r = PaceZoneCalculator.Calculate(new(300, null, [], 1200, 5, null, null, 2026));
        Assert.Contains("zones.goal_too_ambitious", r.Warnings);
        Assert.Equal(PaceZoneCalculator.PredictedRacePace(300, 5), r.Zones.RacePaceSecPerKm);
        Assert.InRange(r.Zones.RacePaceSecPerKm!.Value, 275, 290);
    }

    [Fact]
    public void Predicted_paces_are_ordered_by_distance()
    {
        var paces = new[] { 5, 10, 21.0975, 42.195 }.Select(d => PaceZoneCalculator.PredictedRacePace(300, d)).ToList();
        Assert.Equal(paces.Order(), paces);
        Assert.Equal(321, paces[3]);
    }
}
