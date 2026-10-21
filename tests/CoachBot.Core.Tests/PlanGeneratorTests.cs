using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;
using CoachBot.Core.Safety;

namespace CoachBot.Core.Tests;

public class PlanGeneratorTests
{
    private static readonly SafetyOptions Options = new();
    private static readonly DateOnly Today = new(2026, 10, 21); // Wednesday

    private static readonly TrainingZones PaceZones = new(ZoneBasis.CorosThreshold, 300, 321, 185);
    private static readonly TrainingZones RpeZones = new(ZoneBasis.Rpe, null, null, null);

    private static PlanInputs Inputs(
        int weeks = 16, double baseKm = 35, double longest = 16, int days = 4,
        DayOfWeek longDay = DayOfWeek.Sunday, TrainingZones? zones = null, int history = 8, DayOfWeek raceDay = DayOfWeek.Sunday,
        double raceKm = 42.195)
    {
        // Race in the week `weeks` after plan start (plan starts the Monday after tomorrow).
        var planStart = new DateOnly(2026, 10, 26);
        var raceDate = planStart.AddDays(7 * (weeks - 1) + PlanGenerator.MondayIndex(raceDay));
        return new PlanInputs(Today, raceDate, raceKm, baseKm, longest, history, days, longDay, zones ?? PaceZones);
    }

    public static IEnumerable<object[]> ShorterRaceGrid()
    {
        foreach (var raceKm in new[] { 5.0, 10, 21.0975 })
        foreach (var weeks in new[] { 5, 6, 8, 10, 12, 16 })
        foreach (var baseKm in new[] { 12.0, 20, 35, 50, 70 })
        foreach (var longest in new[] { 6.0, 14, 24 })
        foreach (var days in new[] { 3, 4, 5, 6 })
        {
            yield return [raceKm, weeks, baseKm, longest, days];
        }
    }

    [Theory]
    [MemberData(nameof(ShorterRaceGrid))]
    public void Plans_for_shorter_races_pass_the_validator_with_their_own_limits(double raceKm, int weeks, double baseKm, double longest, int days)
    {
        var options = Options.ForRace(raceKm);
        var generator = new PlanGenerator(options);
        var validator = new PlanValidator(options);

        foreach (var zones in new[] { PaceZones with { RaceDistanceKm = raceKm, RacePaceSecPerKm = null }, RpeZones with { RaceDistanceKm = raceKm } })
        foreach (var longDay in new[] { DayOfWeek.Sunday, DayOfWeek.Saturday, DayOfWeek.Wednesday })
        foreach (var raceDay in new[] { DayOfWeek.Sunday, DayOfWeek.Saturday, DayOfWeek.Monday })
        {
            var input = Inputs(weeks, baseKm, longest, days, longDay, zones, raceDay: raceDay, raceKm: raceKm);
            var draft = generator.Generate(input);
            if (!draft.Feasible)
            {
                continue;
            }

            var violations = validator.ValidatePlan(draft.Workouts, draft.Baseline, zones, new PlanConstraints(input.RaceDate, days));
            Assert.True(violations.Count == 0,
                $"race={raceKm} weeks={weeks} base={baseKm} longest={longest} days={days} long={longDay} race={raceDay} zones={zones.Basis}: "
                + string.Join("; ", violations.Select(v => $"{v.Code} w{v.WeekIndex} {v.Date} {v.Detail}")));
            Assert.All(draft.Workouts.Where(w => w.Type != WorkoutType.Race), w => Assert.True(w.TargetDistanceKm <= options.LongRunCapKm + 0.5));
            Assert.Equal(raceKm, draft.Workouts.Single(w => w.Type == WorkoutType.Race).TargetDistanceKm);
        }
    }

    [Fact]
    public void Half_marathon_plan_has_half_limits_and_race_pace_work()
    {
        var options = Options.ForRace(21.0975);
        var zones = PaceZones with { RaceDistanceKm = 21.0975, RacePaceSecPerKm = 309 };
        var draft = new PlanGenerator(options).Generate(Inputs(weeks: 14, baseKm: 40, longest: 16, days: 5, zones: zones, raceKm: 21.0975));

        Assert.True(draft.Feasible);
        Assert.True(draft.Workouts.Where(w => w.Type == WorkoutType.Long).Max(w => w.TargetDistanceKm) <= 22);
        Assert.Equal(2, draft.Workouts.Where(w => w.Phase == PlanPhase.Taper).Select(w => w.WeekIndex).Distinct().Count());
        var racePace = draft.Workouts.First(w => w.Type == WorkoutType.RacePace);
        Assert.Equal(zones.PaceFor(Intensity.HalfMarathon), new PaceRange(racePace.PaceMinSecPerKm!.Value, racePace.PaceMaxSecPerKm!.Value));
    }

    [Fact]
    public void Short_race_limits_never_exceed_the_configured_marathon_limits()
    {
        var strict = new SafetyOptions { LongRunCapKm = 18, PeakVolumeCapKm = 50, MinWeeksAbsolute = 5 };

        var half = strict.ForRace(21.0975);
        Assert.Equal(18, half.LongRunCapKm);
        Assert.Equal(50, half.PeakVolumeCapKm);
        Assert.Equal(5, half.MinWeeksAbsolute);
        Assert.Same(strict, strict.ForRace(42.195));
        Assert.Equal(16, strict.ForRace(5).LongRunCapKm);
        Assert.Single(strict.ForRace(5).TaperFactors);
    }

    [Fact]
    public void Five_k_plans_build_speed_with_intervals_instead_of_race_pace_sessions()
    {
        var options = Options.ForRace(5);
        var zones = PaceZones with { RaceDistanceKm = 5 };
        var draft = new PlanGenerator(options).Generate(Inputs(weeks: 10, baseKm: 30, longest: 10, days: 4, zones: zones, raceKm: 5));

        Assert.True(draft.Feasible);
        Assert.DoesNotContain(draft.Workouts, w => w.Type == WorkoutType.RacePace);
        Assert.Contains(draft.Workouts, w => w.Type == WorkoutType.Interval);
    }

    public static IEnumerable<object[]> Grid()
    {
        foreach (var weeks in new[] { 8, 10, 12, 16, 20, 26 })
        foreach (var baseKm in new[] { 12.0, 20, 35, 50, 70, 95 })
        foreach (var longest in new[] { 6.0, 14, 24 })
        foreach (var days in new[] { 3, 4, 5, 6 })
        {
            yield return [weeks, baseKm, longest, days];
        }
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Generated_plans_always_pass_the_safety_validator(int weeks, double baseKm, double longest, int days)
    {
        var generator = new PlanGenerator(Options);
        var validator = new PlanValidator(Options);

        foreach (var zones in new[] { PaceZones, RpeZones })
        foreach (var longDay in new[] { DayOfWeek.Sunday, DayOfWeek.Saturday, DayOfWeek.Wednesday })
        foreach (var raceDay in new[] { DayOfWeek.Sunday, DayOfWeek.Saturday, DayOfWeek.Monday })
        {
            var draft = generator.Generate(Inputs(weeks, baseKm, longest, days, longDay, zones, raceDay: raceDay));
            if (!draft.Feasible)
            {
                continue;
            }

            var input = Inputs(weeks, baseKm, longest, days, longDay, zones, raceDay: raceDay);
            var violations = validator.ValidatePlan(draft.Workouts, draft.Baseline, zones, new PlanConstraints(input.RaceDate, days));
            Assert.True(violations.Count == 0,
                $"weeks={weeks} base={baseKm} longest={longest} days={days} long={longDay} race={raceDay} zones={zones.Basis}: "
                + string.Join("; ", violations.Select(v => $"{v.Code} w{v.WeekIndex} {v.Date} {v.Detail}")));
        }
    }

    [Fact]
    public void Plan_ends_with_the_race_on_race_day_and_nothing_after()
    {
        var input = Inputs();
        var draft = new PlanGenerator(Options).Generate(input);

        var last = draft.Workouts.OrderBy(w => w.Date).Last();
        Assert.Equal(WorkoutType.Race, last.Type);
        Assert.Equal(input.RaceDate, last.Date);
        Assert.Equal(42.195, last.TargetDistanceKm);
        Assert.Single(draft.Workouts, w => w.Type == WorkoutType.Race);
    }

    [Fact]
    public void Weekly_volume_never_rises_more_than_ten_percent_and_has_cutbacks()
    {
        var draft = new PlanGenerator(Options).Generate(Inputs(weeks: 20, baseKm: 40));
        var weeks = draft.Workouts.Where(w => w.Type != WorkoutType.Race)
            .GroupBy(w => w.WeekIndex).OrderBy(g => g.Key)
            .Select(g => (g.Key, Km: g.Sum(w => w.TargetDistanceKm), Cutback: g.Any(w => w.IsCutbackWeek), Taper: g.Any(w => w.Phase == PlanPhase.Taper)))
            .ToList();

        Assert.Contains(weeks, w => w.Cutback);
        Assert.Equal(3, weeks.Count(w => w.Taper));
        var peak = weeks.Where(w => !w.Taper).Max(w => w.Km);
        Assert.True(peak > 40, "volume should build from the 40 km base");
        Assert.True(weeks.Last().Km <= peak * 0.5, "race week should be well below peak");
    }

    [Fact]
    public void First_week_starts_from_average_volume_not_more()
    {
        var draft = new PlanGenerator(Options).Generate(Inputs(baseKm: 30));
        var firstWeek = draft.Workouts.Where(w => w.WeekIndex == 0).Sum(w => w.TargetDistanceKm);
        Assert.InRange(firstWeek, 20, 30);
    }

    [Fact]
    public void Too_little_time_for_a_beginner_is_rejected()
    {
        var draft = new PlanGenerator(Options).Generate(Inputs(weeks: 6, baseKm: 25, longest: 12));
        Assert.False(draft.Feasible);
        Assert.Contains("plan.infeasible_too_short", draft.Warnings);
    }

    [Fact]
    public void Short_build_is_allowed_for_an_experienced_runner_with_warning()
    {
        var draft = new PlanGenerator(Options).Generate(Inputs(weeks: 6, baseKm: 55, longest: 24));
        Assert.True(draft.Feasible);
        Assert.Contains("plan.warn_short", draft.Warnings);
    }

    [Fact]
    public void Very_low_logged_volume_is_rejected_until_a_base_is_built()
    {
        var draft = new PlanGenerator(Options).Generate(Inputs(weeks: 24, baseKm: 6, longest: 5));
        Assert.False(draft.Feasible);
        Assert.Contains("plan.infeasible_low_base", draft.Warnings);
    }

    [Fact]
    public void Missing_history_uses_conservative_default_and_warns()
    {
        var draft = new PlanGenerator(Options).Generate(Inputs(baseKm: 0, longest: 0, history: 0));
        Assert.True(draft.Feasible);
        Assert.Contains("plan.warn_little_history", draft.Warnings);
        Assert.Contains("plan.warn_default_volume", draft.Warnings);
        Assert.True(draft.Workouts.Where(w => w.WeekIndex == 0).Sum(w => w.TargetDistanceKm) <= 15);
    }

    [Fact]
    public void Race_too_soon_is_rejected()
    {
        var input = new PlanInputs(Today, Today.AddDays(3), 42.195, 50, 25, 8, 4, DayOfWeek.Sunday, PaceZones);
        var draft = new PlanGenerator(Options).Generate(input);
        Assert.False(draft.Feasible);
        Assert.Contains("plan.infeasible_race_too_soon", draft.Warnings);
    }

    [Fact]
    public void Long_runs_respect_absolute_caps_for_high_volume_runners()
    {
        var draft = new PlanGenerator(Options).Generate(Inputs(weeks: 26, baseKm: 95, longest: 30, days: 6));
        var maxLong = draft.Workouts.Where(w => w.Type == WorkoutType.Long).Max(w => w.TargetDistanceKm);
        Assert.True(maxLong <= Options.LongRunCapKm);
    }

    [Fact]
    public void Pace_and_hr_targets_come_from_zones_and_are_absent_without_data()
    {
        var withPace = new PlanGenerator(Options).Generate(Inputs());
        var easy = withPace.Workouts.First(w => w.Type == WorkoutType.Easy);
        Assert.NotNull(easy.PaceMinSecPerKm);
        Assert.True(easy.PaceMinSecPerKm < easy.PaceMaxSecPerKm);
        Assert.NotNull(easy.HrMax);

        var rpeOnly = new PlanGenerator(Options).Generate(Inputs(zones: RpeZones));
        Assert.All(rpeOnly.Workouts, w => Assert.Null(w.PaceMinSecPerKm));
    }
}
