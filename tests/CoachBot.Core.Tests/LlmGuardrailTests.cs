using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Safety;
using CoachBot.Core.Services;

namespace CoachBot.Core.Tests;

/// <summary>The checks that stand between an LLM-designed plan or adaptation and the athlete.</summary>
public class LlmGuardrailTests
{
    private static readonly SafetyOptions Options = new();
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly TrainingZones Zones = new(ZoneBasis.CorosThreshold, 300, 321, 185);

    private static PlanDraft Baseline(int weeks = 16, int days = 4)
    {
        var start = new DateOnly(2026, 10, 26);
        var race = start.AddDays(7 * (weeks - 1) + 6);
        return new PlanGenerator(Options).Generate(new PlanInputs(Today, race, 42.195, 35, 16, 8, days, DayOfWeek.Sunday, Zones));
    }

    private static PlanDesign DesignOf(PlanDraft draft) => new("r", PlanDesignMapper.ToDesign(draft.Workouts));

    private static IReadOnlyList<Violation> Validate(PlanDraft draft, PlanDesign design, int days = 4)
    {
        var (workouts, errors) = PlanDesignMapper.Map(design, draft.StartDate, draft.EndDate, 42.195, Zones);
        return [.. errors, .. new PlanValidator(Options).ValidatePlan(workouts, draft.Baseline, Zones, new PlanConstraints(draft.EndDate, days))];
    }

    private static PlanDesign Modify(PlanDesign design, int week, Func<WeekDesign, WeekDesign> change) =>
        design with { Weeks = [.. design.Weeks!.Select(w => w.Week == week ? change(w) : w)] };

    [Fact]
    public void Baseline_round_trips_through_the_design_format_and_stays_valid()
    {
        var draft = Baseline();
        Assert.Empty(Validate(draft, DesignOf(draft)));
    }

    [Fact]
    public void Mapper_sets_dates_paces_and_always_places_the_race_on_race_day()
    {
        var draft = Baseline();
        var design = DesignOf(draft);
        var withoutRace = Modify(design, design.Weeks!.Count, w => w with { Sessions = [.. w.Sessions!.Where(x => x.Type != "race")] });

        var (workouts, errors) = PlanDesignMapper.Map(withoutRace, draft.StartDate, draft.EndDate, 42.195, Zones);

        Assert.Empty(errors);
        var race = Assert.Single(workouts, w => w.Type == WorkoutType.Race);
        Assert.Equal(draft.EndDate, race.Date);
        Assert.All(workouts.Where(w => w.Type == WorkoutType.Easy), w => Assert.NotNull(w.PaceMinSecPerKm));
    }

    [Fact]
    public void Structural_mistakes_are_reported_for_repair()
    {
        var draft = Baseline();
        var design = DesignOf(draft);
        var broken = Modify(design, 2, w => w with { Sessions = [new("someday", "easy", 8), new("tue", "sprint", 5)] });
        broken = broken with { Weeks = [.. broken.Weeks!.Where(w => w.Week != 5)] };

        var codes = Validate(draft, broken).Select(v => v.Code).ToList();

        Assert.Contains("design_unknown_day", codes);
        Assert.Contains("design_unknown_type", codes);
        Assert.Contains("design_weeks_invalid", codes);
    }

    [Theory]
    [InlineData("too_many_run_days")]
    [InlineData("run_too_short")]
    [InlineData("long_run_over_cap")]
    [InlineData("volume_increase_too_large")]
    [InlineData("race_week_not_tapered")]
    public void Unsafe_llm_designs_are_rejected(string expected)
    {
        var draft = Baseline();
        var design = DesignOf(draft);
        var last = design.Weeks!.Count;
        var broken = expected switch
        {
            "too_many_run_days" => Modify(design, 3, w => w with { Sessions = [.. w.Sessions!, new("mon", "easy", 5), new("fri", "easy", 5)] }),
            "run_too_short" => Modify(design, 3, w => w with { Sessions = [.. w.Sessions!.Select(x => x.Type == "easy" ? x with { Km = 2 } : x)] }),
            // A 35 km run labelled "easy" is still a long run.
            "long_run_over_cap" => Modify(design, 3, w => w with { Sessions = [.. w.Sessions!.Select(x => x.Type == "easy" ? x with { Km = 35 } : x)] }),
            "volume_increase_too_large" => Modify(design, 2, w => w with { Sessions = [.. w.Sessions!.Select(x => x with { Km = x.Km * 1.5 })] }),
            "race_week_not_tapered" => Modify(design, last, w => w with { Sessions = [.. w.Sessions!.Select(x => x.Type == "easy" ? x with { Km = 20 } : x)] }),
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };

        Assert.Contains(expected, Validate(draft, broken).Where(v => !v.IsGuideline).Select(v => v.Code));
    }

    [Theory]
    [InlineData("hard_days_back_to_back")]
    [InlineData("hard_session_before_race")]
    [InlineData("too_many_quality_sessions")]
    [InlineData("too_little_easy_running")]
    public void Coaching_heuristics_are_reported_as_guidelines_and_never_enforced(string expected)
    {
        var draft = Baseline();
        var design = DesignOf(draft);
        var last = design.Weeks!.Count;
        SessionDesign Harder(SessionDesign s) => s.Type == "easy" ? s with { Type = "tempo" } : s;
        var bold = expected switch
        {
            // The day before the long run becomes a tempo: a cumulative-fatigue weekend.
            "hard_days_back_to_back" => Modify(design, 6, w => w with { Sessions = [.. w.Sessions!.Select(x => x.Day == DayBefore(w, "long") ? Harder(x) : x)] }),
            // A short sharpening tempo the day before the race.
            "hard_session_before_race" => Modify(design, last, w => w with
            {
                Sessions = [.. w.Sessions!.Select(x => x == w.Sessions!.Last(s => s.Type != "race") ? x with { Day = DayBefore(w, "race"), Type = "tempo" } : x)],
            }),
            "too_many_quality_sessions" or "too_little_easy_running" => Modify(design, 2, w => w with { Sessions = [.. w.Sessions!.Select(Harder)] }),
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };

        var violations = Validate(draft, bold);

        Assert.Contains(expected, violations.Select(v => v.Code));
        Assert.All(violations, v => Assert.True(v.IsGuideline, v.Code));
    }

    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// <summary>The weekday before the session of <paramref name="type"/> in that week.</summary>
    private static string DayBefore(WeekDesign week, string type) => Days[Array.IndexOf(Days, week.Sessions!.First(s => s.Type == type).Day) - 1];

    [Fact]
    public void Clamp_cuts_an_overreaching_week_to_the_ceiling_and_keeps_the_rest_of_the_design()
    {
        var draft = Baseline();
        var design = DesignOf(draft) with { Weeks = [.. DesignOf(draft).Weeks!.Select(w => w with { Sessions = [.. w.Sessions!.Select(s => s with { Notes = $"AI w{w.Week}" })] })] };
        var bold = Modify(design, 6, w => w with { Sessions = [.. w.Sessions!.Select(x => x with { Km = x.Km * 1.6 })] });
        var (workouts, errors) = PlanDesignMapper.Map(bold, draft.StartDate, draft.EndDate, 42.195, Zones);
        var constraints = new PlanConstraints(draft.EndDate, 4);
        var validator = new PlanValidator(Options);
        Assert.Contains(validator.ValidatePlan(workouts, draft.Baseline, Zones, constraints), v => !v.IsGuideline);

        var result = PlanClamp.Apply(workouts, errors, draft, validator, Zones, constraints)!;

        Assert.DoesNotContain(validator.ValidatePlan(result.Workouts, draft.Baseline, Zones, constraints), v => !v.IsGuideline);
        Assert.Contains(5, result.ClampedWeeks);
        Assert.Empty(result.ReplacedWeeks);
        // Still the LLM's sessions on the LLM's days, only shorter.
        Assert.All(result.Workouts.Where(w => w.Type != WorkoutType.Race), w => Assert.Equal($"AI w{w.WeekIndex + 1}", w.Description));
        var week = result.Workouts.Where(w => w.WeekIndex == 5).ToList();
        Assert.Equal(workouts.Count(w => w.WeekIndex == 5), week.Count);
        Assert.True(week.Sum(w => w.TargetDistanceKm) < workouts.Where(w => w.WeekIndex == 5).Sum(w => w.TargetDistanceKm));
        Assert.Equal(workouts.Where(w => w.WeekIndex < 5).Sum(w => w.TargetDistanceKm), result.Workouts.Where(w => w.WeekIndex < 5).Sum(w => w.TargetDistanceKm));
    }

    [Fact]
    public void Clamp_brings_a_plan_that_is_too_big_everywhere_within_every_limit()
    {
        var draft = Baseline();
        var doubled = DesignOf(draft) with { Weeks = [.. DesignOf(draft).Weeks!.Select(w => w with { Sessions = [.. w.Sessions!.Select(s => s with { Km = s.Km * 2 })] })] };
        var (workouts, errors) = PlanDesignMapper.Map(doubled, draft.StartDate, draft.EndDate, 42.195, Zones);
        var constraints = new PlanConstraints(draft.EndDate, 4);
        var validator = new PlanValidator(Options);

        var result = PlanClamp.Apply(workouts, errors, draft, validator, Zones, constraints)!;

        Assert.DoesNotContain(validator.ValidatePlan(result.Workouts, draft.Baseline, Zones, constraints), v => !v.IsGuideline);
        Assert.NotEmpty(result.ClampedWeeks);
        Assert.Equal(workouts.Select(w => (w.Date, w.Type)), result.Workouts.Select(w => (w.Date, w.Type)));
    }

    [Theory]
    [InlineData(42.195, 20, 3, 18)]
    [InlineData(42.195, 60, 6, 20)]
    [InlineData(21.0975, 25, 4, 12)]
    [InlineData(21.0975, 50, 5, 10)]
    [InlineData(10, 15, 3, 10)]
    [InlineData(10, 45, 5, 8)]
    [InlineData(5, 12, 3, 8)]
    [InlineData(5, 40, 6, 6)]
    public void Clamp_always_finds_a_valid_plan_for_an_overreaching_design(double raceKm, double weeklyKm, int days, int weeks)
    {
        var options = Options.ForRace(raceKm);
        var race = new DateOnly(2026, 10, 26).AddDays(7 * (weeks - 1) + 6);
        var draft = new PlanGenerator(options).Generate(new PlanInputs(Today, race, raceKm, weeklyKm, weeklyKm * 0.35, 8, days, DayOfWeek.Sunday, Zones));
        Assert.True(draft.Feasible);
        var design = DesignOf(draft);
        var bold = design with { Weeks = [.. design.Weeks!.Select(w => w with { Sessions = [.. w.Sessions!.Select(s => s with { Km = s.Km * (1.3 + 0.1 * (w.Week % 4)) })] })] };
        var (workouts, errors) = PlanDesignMapper.Map(bold, draft.StartDate, draft.EndDate, raceKm, Zones);
        var constraints = new PlanConstraints(draft.EndDate, days);
        var validator = new PlanValidator(options);

        var result = PlanClamp.Apply(workouts, errors, draft, validator, Zones, constraints);

        Assert.NotNull(result);
        Assert.DoesNotContain(validator.ValidatePlan(result.Workouts, draft.Baseline, Zones, constraints), v => !v.IsGuideline);
        // Too much volume is cut, never thrown away.
        Assert.NotEmpty(result.ClampedWeeks);
        Assert.Empty(result.ReplacedWeeks);
    }

    [Fact]
    public void Clamp_replaces_a_week_it_cannot_cut_with_the_baseline_week()
    {
        var draft = Baseline();
        var design = DesignOf(draft);
        var broken = Modify(design, 4, w => w with { Sessions = [.. w.Sessions!.Select(x => x.Type == "easy" ? x with { Type = "sprint" } : x)] });
        var (workouts, errors) = PlanDesignMapper.Map(broken, draft.StartDate, draft.EndDate, 42.195, Zones);
        var constraints = new PlanConstraints(draft.EndDate, 4);
        var validator = new PlanValidator(Options);
        Assert.Contains(errors, e => e.Code == "design_unknown_type" && e.WeekIndex == 3);

        var result = PlanClamp.Apply(workouts, errors, draft, validator, Zones, constraints)!;

        Assert.Equal([3], result.ReplacedWeeks);
        Assert.Equal(draft.Workouts.Where(w => w.WeekIndex == 3).Select(w => (w.Date, w.Type, w.TargetDistanceKm)),
            result.Workouts.Where(w => w.WeekIndex == 3).Select(w => (w.Date, w.Type, w.TargetDistanceKm)));
        Assert.DoesNotContain(validator.ValidatePlan(result.Workouts, draft.Baseline, Zones, constraints), v => !v.IsGuideline);
    }

    [Fact]
    public void Approved_increases_must_still_respect_progression_and_never_touch_the_taper()
    {
        var draft = Baseline();
        var before = draft.Workouts;
        var validator = new PlanValidator(Options);

        PlannedWorkout Find(Func<PlannedWorkout, bool> p) => before.First(p);
        double Km(int week) => before.Where(w => w.WeekIndex == week && w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm);
        bool Full(int week) => before.Any(w => w.WeekIndex == week) && !before.Any(w => w.WeekIndex == week && (w.IsCutbackWeek || w.Phase == PlanPhase.Taper));

        // A full week that still has progression headroom (e.g. a plateau at peak volume).
        var headroomWeek = Enumerable.Range(1, 20).First(i => Full(i) && Full(i - 1) && Km(i) + 2 <= Km(i - 1) * (1 + Options.MaxWeeklyIncreasePct));
        var buildEasy = Find(w => w.WeekIndex == headroomWeek && w.Type == WorkoutType.Easy);
        var taperEasy = Find(w => w.Phase == PlanPhase.Taper && w.Type == WorkoutType.Easy);
        var cutbackEasy = Find(w => w.IsCutbackWeek && w.Type == WorkoutType.Easy);

        IReadOnlyList<Violation> After(PlannedWorkout target, double km)
        {
            var change = new ProposedChange(target.Id, target.Date, ChangeAction.IncreaseDistance, target.Type, target.Type, target.TargetDistanceKm, km);
            return validator.ValidateAdaptation(before, PlanAdjuster.Preview(before, [change], Zones), Zones, allowIncrease: true);
        }

        Assert.Empty(After(buildEasy, buildEasy.TargetDistanceKm + 1));
        Assert.Contains("adaptation_increase_too_large", After(buildEasy, buildEasy.TargetDistanceKm + 12).Select(v => v.Code));
        Assert.Contains("taper_increase", After(taperEasy, taperEasy.TargetDistanceKm + 1).Select(v => v.Code));
        Assert.Contains("cutback_increase", After(cutbackEasy, cutbackEasy.TargetDistanceKm + 1).Select(v => v.Code));

        var withoutApproval = validator.ValidateAdaptation(before, PlanAdjuster.Preview(before,
            [new ProposedChange(buildEasy.Id, buildEasy.Date, ChangeAction.IncreaseDistance, buildEasy.Type, buildEasy.Type, buildEasy.TargetDistanceKm, buildEasy.TargetDistanceKm + 1)], Zones),
            Zones, allowIncrease: false);
        Assert.Contains("adaptation_increases_volume", withoutApproval.Select(v => v.Code));
    }

    [Fact]
    public void Approved_upgrades_at_the_same_distance_are_still_checked()
    {
        var draft = Baseline();
        var before = draft.Workouts;
        var validator = new PlanValidator(Options);

        IReadOnlyList<Violation> Upgrade(PlannedWorkout target)
        {
            var change = new ProposedChange(target.Id, target.Date, ChangeAction.Upgrade, target.Type, WorkoutType.Interval, target.TargetDistanceKm, target.TargetDistanceKm);
            return validator.ValidateAdaptation(before, PlanAdjuster.Preview(before, [change], Zones), Zones, allowIncrease: true);
        }

        Assert.Contains("taper_increase", Upgrade(before.First(w => w.Phase == PlanPhase.Taper && w.Type == WorkoutType.Easy)).Select(v => v.Code));
        Assert.Contains("cutback_increase", Upgrade(before.First(w => w.IsCutbackWeek && w.Type == WorkoutType.Easy)).Select(v => v.Code));
    }
}
