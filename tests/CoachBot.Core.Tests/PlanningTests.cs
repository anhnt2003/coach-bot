using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Services;

namespace CoachBot.Core.Tests;

/// <summary>The coach's pace table, and the integrity gates on its plan designs and plan changes.</summary>
public class PlanningTests
{
    private const double HalfKm = 21.0975;

    private static readonly PaceTable HalfPaces = new(
    [
        new(Intensity.Recovery, 390, 426),
        new(Intensity.Easy, 354, 396, 120, 145),
        new(Intensity.Steady, 330, 345),
        new(Intensity.HalfMarathon, 300, 306, 160, 170),
        new(Intensity.Threshold, 294, 309),
        new(Intensity.FiveK, 278, 290),
    ], HalfKm);

    private static readonly List<WorkoutBlock> FiveByOneK =
    [
        new(1, new(Intensity.Easy, Km: 2)),
        new(5, new(Intensity.FiveK, Km: 1), new(Intensity.Recovery, Minutes: 2)),
        new(1, new(Intensity.Easy, Km: 2)),
    ];

    [Fact]
    public void Pace_table_looks_up_targets_and_runs_unknown_intensities_by_effort()
    {
        Assert.Equal(new PaceRange(354, 396), HalfPaces.PaceFor(Intensity.Easy));
        Assert.Equal(new HrRange(120, 145), HalfPaces.HrFor(Intensity.Easy));
        Assert.Null(HalfPaces.HrFor(Intensity.Steady));
        Assert.Null(HalfPaces.PaceFor(Intensity.Repetition));

        Assert.Equal(Intensity.Easy, HalfPaces.DefaultIntensity(WorkoutType.Long));
        Assert.Equal(Intensity.HalfMarathon, HalfPaces.DefaultIntensity(WorkoutType.RacePace));
        Assert.Equal(Intensity.FiveK, HalfPaces.DefaultIntensity(WorkoutType.Interval));
        Assert.Null(HalfPaces.DefaultIntensity(WorkoutType.Rest));

        Assert.Equal(375, HalfPaces.ConversionPaceSecPerKm(Intensity.Easy));
        Assert.Equal(PaceTable.DefaultConversionPaceSecPerKm, HalfPaces.ConversionPaceSecPerKm(Intensity.Repetition));
    }

    [Fact]
    public void Pace_table_round_trips_through_the_plan_snapshot()
    {
        var json = CoachJson.Serialize(new PlanSnapshot(HalfPaces, HalfKm, 40, 16, 8, 50));
        Assert.Contains("\"intensity\":\"five_k\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("race_intensity", json, StringComparison.Ordinal);

        var back = CoachJson.Deserialize<PlanSnapshot>(json)!;
        Assert.Equal(HalfPaces.Entries, back.Paces!.Entries);

        // A plan stored before the coach set paces has no table: its sessions are run by effort.
        var old = new TrainingPlan { InputsJson = """{"zones":{"basis":"coros_threshold","threshold_pace_sec_per_km":300}}""" };
        Assert.Empty(PlanSnapshot.PacesOf(old).Entries);
    }

    [Fact]
    public void Steps_are_measured_with_the_pace_table()
    {
        var recoveryKm = 5 * 2 * 60.0 / HalfPaces.ConversionPaceSecPerKm(Intensity.Recovery);
        Assert.Equal(9 + recoveryKm, WorkoutStructure.TotalKm(FiveByOneK, HalfPaces), 3);

        var resolved = WorkoutStructure.Resolve(FiveByOneK, HalfPaces);
        Assert.Equal(278, resolved[1].Work.PaceFast);
        Assert.Equal(Intensity.FiveK, WorkoutStructure.MainIntensity(resolved));

        Assert.Empty(WorkoutStructure.Problems(FiveByOneK));
        Assert.NotEmpty(WorkoutStructure.Problems([new(0, new(Intensity.Easy, Km: 2))]));
        Assert.NotEmpty(WorkoutStructure.Problems([new(1, new(Intensity.Easy, Km: 2, Minutes: 10))]));
    }

    // ---- plan designs ----

    private static readonly DateOnly Start = new(2026, 10, 26); // Monday
    private static readonly DateOnly Race = new(2026, 11, 8); // Sunday of week 2

    private static readonly List<PaceDesign> Paces =
    [
        new("easy", 354, 396, 120, 145),
        new("recovery", 390, 426),
        new("race_pace", 300, 306, 160, 170),
        new("five_k", 278, 290),
    ];

    private static PlanDesign Design(IReadOnlyList<SessionDesign>? week1 = null, IReadOnlyList<PaceDesign>? paces = null, int weeks = 2) => new(
        "AI rationale",
        [.. Enumerable.Range(1, weeks).Select(n => new WeekDesign(n, n == weeks ? "taper" : "build", false, $"focus {n}", n == 1
            ? week1 ?? [new SessionDesign("tue", "easy", 8, "relaxed"), new SessionDesign("sat", "long", 14)]
            : [new SessionDesign("wed", "easy", 6), new SessionDesign("sun", "race", 21)]))],
        paces ?? Paces);

    [Fact]
    public void A_design_becomes_workouts_with_the_coaches_own_targets()
    {
        var design = Design(
        [
            new SessionDesign("tue", "easy", 8.3, "relaxed"),
            new SessionDesign("thu", "race_pace", 99, "Hold back", "3 × 2 km at half-marathon pace",
            [
                new BlockDesign(1, new PartDesign("easy", Km: 2)),
                new BlockDesign(3, new PartDesign("race_pace", Km: 2), new PartDesign("recovery", Minutes: 2)),
                new BlockDesign(1, new PartDesign("easy", Km: 2)),
            ]),
            new SessionDesign("sat", "long", 14, Intensity: "recovery"),
        ]);

        var mapped = PlanDesignMapper.Map(design, Start, Race, HalfKm);

        Assert.Empty(mapped.Errors);
        var easy = mapped.Workouts.Single(w => w.Date == Start.AddDays(1));
        Assert.Equal((8, 354, 396, 120, 145), (easy.TargetDistanceKm, easy.PaceMinSecPerKm!.Value, easy.PaceMaxSecPerKm!.Value, easy.HrMin!.Value, easy.HrMax!.Value));
        Assert.Equal((int)Math.Round(8 * 375 / 60.0), easy.TargetDurationMinutes);
        Assert.Equal("relaxed", easy.Description);

        var racePace = mapped.Workouts.Single(w => w.Type == WorkoutType.RacePace);
        var blocks = WorkoutStructure.Parse(racePace.StepsJson)!;
        Assert.Equal(Intensity.HalfMarathon, blocks[1].Work.Intensity);
        Assert.Equal((300, 160), (blocks[1].Work.PaceFast!.Value, racePace.HrMin!.Value));
        Assert.InRange(racePace.TargetDistanceKm, 11, 12); // from the steps, not the LLM's "99"
        Assert.Equal("3 × 2 km at half-marathon pace", racePace.Title);

        // A plain run at a named intensity takes that intensity's targets.
        Assert.Equal(390, mapped.Workouts.Single(w => w.Type == WorkoutType.Long).PaceMinSecPerKm);

        var race = mapped.Workouts.Single(w => w.Type == WorkoutType.Race);
        Assert.Equal((Race, HalfKm), (race.Date, race.TargetDistanceKm));
        Assert.Equal(mapped.Workouts.OrderBy(w => w.Date), mapped.Workouts);
    }

    [Theory]
    [InlineData("missing week", "design_weeks_invalid")]
    [InlineData("unknown type", "design_unknown_type")]
    [InlineData("unknown day", "design_unknown_day")]
    [InlineData("race on the wrong day", "race_on_wrong_day")]
    [InlineData("two sessions on one day", "two_sessions_same_day")]
    [InlineData("no distance", "session_km_invalid")]
    [InlineData("unknown step intensity", "design_unknown_intensity")]
    [InlineData("zero repeats", "steps_invalid")]
    [InlineData("pace fast slower than slow", "pace_invalid")]
    [InlineData("pace in minutes", "pace_invalid")]
    [InlineData("race pace given twice", "pace_duplicate")]
    [InlineData("half a heart-rate range", "hr_invalid")]
    public void Unusable_designs_are_rejected_with_a_reason(string problem, string code)
    {
        var design = problem switch
        {
            "missing week" => Design(weeks: 2) with { Weeks = [Design().Weeks![0]] },
            "unknown type" => Design([new SessionDesign("tue", "fartlek", 8)]),
            "unknown day" => Design([new SessionDesign("someday", "easy", 8)]),
            "race on the wrong day" => Design([new SessionDesign("sat", "race", 21)]),
            "two sessions on one day" => Design([new SessionDesign("tue", "easy", 8), new SessionDesign("tue", "tempo", 6)]),
            "no distance" => Design([new SessionDesign("tue", "easy", 0)]),
            "unknown step intensity" => Design([new SessionDesign("tue", "interval", 8, Steps: [new BlockDesign(4, new PartDesign("warp_speed", Km: 1))])]),
            "zero repeats" => Design([new SessionDesign("tue", "interval", 8, Steps: [new BlockDesign(0, new PartDesign("five_k", Km: 1))])]),
            "pace fast slower than slow" => Design(paces: [new("easy", 400, 350)]),
            "pace in minutes" => Design(paces: [new("easy", 5, 6)]),
            "race pace given twice" => Design(paces: [new("race_pace", 300, 306), new("half_marathon", 301, 307)]),
            "half a heart-rate range" => Design(paces: [new("easy", 354, 396, HrMin: 120)]),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        var errors = PlanDesignMapper.Map(design, Start, Race, HalfKm).Errors;

        Assert.Contains(errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }

    [Fact]
    public void How_much_the_athlete_runs_is_not_checked()
    {
        // A huge jump and a long run in an easy week are coaching choices, not integrity problems.
        var design = Design([new SessionDesign("mon", "long", 35), new SessionDesign("tue", "long", 35), new SessionDesign("wed", "interval", 25)]);
        Assert.Empty(PlanDesignMapper.Map(design, Start, Race, HalfKm).Errors);
    }

    // ---- plan changes ----

    private static readonly DateOnly Today = new(2026, 10, 27); // Tuesday

    private static (TrainingPlan Plan, PlannedWorkout Tempo, PlannedWorkout Easy, PlannedWorkout Done, PlannedWorkout RaceDay) Plan()
    {
        var plan = new TrainingPlan { StartDate = Start, EndDate = Race, Status = PlanStatus.Active };
        PlannedWorkout W(DateOnly date, WorkoutType type, double km, WorkoutStatus status = WorkoutStatus.Planned)
        {
            var w = new PlannedWorkout { PlanId = plan.Id, Date = date, Type = type, TargetDistanceKm = km, Status = status, Title = $"{type} {km}" };
            plan.Workouts.Add(w);
            return w;
        }

        var done = W(Start, WorkoutType.Easy, 6, WorkoutStatus.Completed);
        var tempo = W(Today, WorkoutType.Tempo, 8);
        var easy = W(Today.AddDays(2), WorkoutType.Easy, 8);
        W(Today.AddDays(4), WorkoutType.Long, 16);
        var race = W(Race, WorkoutType.Race, HalfKm);
        return (plan, tempo, easy, done, race);
    }

    private static (List<MappedChange> Changes, List<string> Errors) MapChanges(TrainingPlan plan, params ChangeDesign[] changes) =>
        WorkoutChangeMapper.Map(changes, plan,
            plan.Workouts.Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Race && w.Date >= Today).Select(w => w.Id).ToHashSet(),
            HalfPaces, Today, Race);

    private static ChangeDesign Change(PlannedWorkout w, string type = "easy", double km = 6, DateOnly? date = null, bool needsApproval = false) =>
        new(w.Id.ToString(), (date ?? w.Date).ToString("yyyy-MM-dd"), type, km, Reason: "tired", NeedsApproval: needsApproval);

    [Fact]
    public void A_change_becomes_the_complete_new_state_of_the_session()
    {
        var (plan, tempo, _, _, _) = Plan();

        var (changes, errors) = MapChanges(plan, Change(tempo, "easy", 6.3));

        Assert.Empty(errors);
        var c = Assert.Single(changes);
        Assert.Same(tempo, c.Target);
        Assert.Equal((Today, WorkoutType.Easy, 6, 354, 120), (c.After.Date, c.After.Type, c.After.Km, c.After.PaceMin!.Value, c.After.HrMin!.Value));
        Assert.False(c.NeedsApproval);
        Assert.Equal("tired", c.Reason);
    }

    [Fact]
    public void Resting_a_session_clears_its_distance_and_targets()
    {
        var (plan, tempo, _, _, _) = Plan();

        var c = Assert.Single(MapChanges(plan, Change(tempo, "rest", 8)).Changes);

        Assert.Equal((WorkoutType.Rest, 0.0), (c.After.Type, c.After.Km));
        Assert.Null(c.After.PaceMin);
        Assert.Null(c.After.Steps);
    }

    [Fact]
    public void A_structured_change_takes_its_distance_and_pace_from_the_steps()
    {
        var (plan, tempo, _, _, _) = Plan();
        var steps = new[] { new BlockDesign(1, new PartDesign("easy", Km: 2)), new BlockDesign(4, new PartDesign("five_k", Km: 1), new PartDesign("recovery", Km: 0.5)) };

        var c = Assert.Single(MapChanges(plan, Change(tempo, "interval", 99) with { Steps = steps }).Changes);

        Assert.Equal((8, 278), (c.After.Km, c.After.PaceMin!.Value));
        Assert.NotNull(c.After.Steps);
    }

    [Fact]
    public void Swapping_two_sessions_is_fine_but_landing_on_a_busy_day_is_not()
    {
        var (plan, tempo, easy, _, _) = Plan();

        Assert.Empty(MapChanges(plan, Change(tempo, "tempo", 8, easy.Date), Change(easy, "easy", 8, tempo.Date)).Errors);
        Assert.Contains(MapChanges(plan, Change(tempo, "tempo", 8, easy.Date)).Errors, e => e.StartsWith("change_duplicate_date", StringComparison.Ordinal));
        // Moving onto a day whose session becomes rest is fine.
        Assert.Empty(MapChanges(plan, Change(tempo, "tempo", 8, easy.Date), Change(easy, "rest", 0)).Errors);
    }

    [Fact]
    public void Changes_to_sessions_that_cannot_change_are_rejected()
    {
        var (plan, tempo, _, done, race) = Plan();

        Assert.Contains(MapChanges(plan, Change(done)).Errors, e => e.StartsWith("change_unknown_workout", StringComparison.Ordinal));
        Assert.Contains(MapChanges(plan, Change(race)).Errors, e => e.StartsWith("change_unknown_workout", StringComparison.Ordinal));
        Assert.Contains(MapChanges(plan, Change(tempo) with { WorkoutId = Guid.NewGuid().ToString() }).Errors, e => e.StartsWith("change_unknown_workout", StringComparison.Ordinal));
        Assert.Contains(MapChanges(plan, Change(tempo), Change(tempo)).Errors, e => e.StartsWith("change_duplicate_workout", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026-10-26", "change_date_out_of_range")] // yesterday
    [InlineData("2026-11-09", "change_date_out_of_range")] // after the race
    [InlineData("27/10/2026", "change_date_out_of_range")]
    public void A_change_lands_between_today_and_race_day(string date, string code)
    {
        var (plan, tempo, _, _, _) = Plan();
        Assert.Contains(MapChanges(plan, Change(tempo) with { Date = date }).Errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("race", 21, "change_unknown_type")]
    [InlineData("jog", 6, "change_unknown_type")]
    [InlineData("easy", 0, "change_km_invalid")]
    public void A_change_names_a_real_session_type_and_distance(string type, double km, string code)
    {
        var (plan, tempo, _, _, _) = Plan();
        Assert.Contains(MapChanges(plan, Change(tempo, type, km)).Errors, e => e.StartsWith(code, StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_load_is_the_coaches_call()
    {
        var (plan, tempo, _, _, _) = Plan();
        Assert.Empty(MapChanges(plan, Change(tempo, "long", 30)).Errors);
    }
}
