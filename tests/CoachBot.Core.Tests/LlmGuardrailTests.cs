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
    [InlineData("hard_days_back_to_back")]
    [InlineData("hard_session_before_race")]
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
            "hard_days_back_to_back" => Modify(design, 3, w => w with { Sessions = [.. w.Sessions!.Where(x => x.Day != "sat"), new("sat", "tempo", 6)] }),
            "hard_session_before_race" => Modify(design, last, w => w with { Sessions = [.. w.Sessions!, new("fri", "tempo", 6)] }),
            "race_week_not_tapered" => Modify(design, last, w => w with { Sessions = [.. w.Sessions!.Select(x => x.Type == "easy" ? x with { Km = 20 } : x)] }),
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };

        Assert.Contains(expected, Validate(draft, broken).Select(v => v.Code));
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
