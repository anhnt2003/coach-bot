using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;

namespace CoachBot.Core.Safety;

public sealed record Violation(string Code, int? WeekIndex, DateOnly? Date, string Detail);

/// <summary>Athlete-specific limits a plan must respect in addition to the generic safety options.</summary>
public sealed record PlanConstraints(DateOnly RaceDate, int RunDaysPerWeek);

/// <summary>
/// Hard safety gate. Every plan (rule-based or designed by the LLM) and every adaptation is checked here before it
/// is stored or applied. The checks look at what a session actually is (distance, steps, intensity, date), not at its
/// label, so a 35 km run labelled "easy" is still a long run and intervals hidden in an "easy" run are rejected.
/// Pass options already adjusted for the goal distance (<see cref="SafetyOptions.ForRace"/>).
/// </summary>
public sealed class PlanValidator(SafetyOptions options)
{
    // Workout distances are rounded to 0.5 km, so allow one rounding step of slack.
    private const double Tolerance = 0.51;
    private const double MinRunKm = 3;

    public IReadOnlyList<Violation> ValidatePlan(
        IReadOnlyList<PlannedWorkout> workouts, PlanBaseline baseline, TrainingZones zones, PlanConstraints? constraints = null)
    {
        var violations = new List<Violation>();
        CheckStructure(workouts, violations);
        CheckSteps(workouts, zones, violations);
        CheckSpacing(workouts, violations);
        if (constraints is not null)
        {
            CheckRace(workouts, constraints, violations);
            CheckRunDays(workouts, constraints, violations);
        }

        var refKm = baseline.WeeklyKm;
        var refLong = baseline.LongRunKm;
        var peakKm = baseline.WeeklyKm;
        double? prevTaperKm = null;
        var weeks = workouts.GroupBy(w => w.WeekIndex).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();

        foreach (var items in weeks)
        {
            var weekIndex = items[0].WeekIndex;
            var km = WeekKm(items);
            var longKm = LongKm(items);
            var taper = items.Any(w => w.Phase == PlanPhase.Taper);
            var cutback = items.Any(w => w.IsCutbackWeek);

            CheckQualityCount(weekIndex, items, violations);
            CheckLongRunCaps(weekIndex, longKm, zones, violations);
            CheckIntensityCaps(items, km, zones, violations);
            if (!items.Any(w => w.Type == WorkoutType.Race))
            {
                CheckEasyShare(weekIndex, items, km, zones, violations);
            }

            if (taper)
            {
                if (km > peakKm * 0.85 + Tolerance)
                {
                    violations.Add(new("taper_not_reduced", weekIndex, null, $"{km:0.#} km vs peak {peakKm:0.#} km"));
                }

                if (prevTaperKm is double prev && km > prev + Tolerance)
                {
                    violations.Add(new("taper_increases", weekIndex, null, $"{km:0.#} km after {prev:0.#} km"));
                }

                prevTaperKm = km;
                continue;
            }

            if (longKm > options.LongRunHardMaxShare * km + Tolerance)
            {
                violations.Add(new("long_run_share_too_high", weekIndex, null, $"{longKm:0.#} of {km:0.#} km"));
            }

            if (cutback)
            {
                if (km > refKm + Tolerance)
                {
                    violations.Add(new("cutback_not_reduced", weekIndex, null, $"{km:0.#} km vs {refKm:0.#} km"));
                }

                continue;
            }

            var limit = Math.Min(refKm * (1 + options.MaxWeeklyIncreasePct), refKm + options.MaxWeeklyIncreaseKm);
            if (km > limit + Tolerance)
            {
                violations.Add(new("volume_increase_too_large", weekIndex, null, $"{km:0.#} km vs limit {limit:0.#} km"));
            }

            if (longKm > refLong + options.LongRunMaxIncreaseKm + Tolerance)
            {
                violations.Add(new("long_run_increase_too_large", weekIndex, null, $"{longKm:0.#} km after {refLong:0.#} km"));
            }

            refKm = km;
            refLong = longKm;
            peakKm = Math.Max(peakKm, km);
        }

        // The race week must be a real taper regardless of how weeks are labelled; for plans of 8+ weeks the week
        // before it too.
        if (constraints is not null && weeks.Count > 0)
        {
            var nonRaceWeeks = weeks.Select(WeekKm).ToList();
            var peak = Math.Max(baseline.WeeklyKm, nonRaceWeeks.Take(Math.Max(1, weeks.Count - 1)).Max());
            if (nonRaceWeeks[^1] > peak * options.RaceWeekMaxShare + Tolerance)
            {
                violations.Add(new("race_week_not_tapered", weeks[^1][0].WeekIndex, null, $"{nonRaceWeeks[^1]:0.#} km vs peak {peak:0.#} km"));
            }

            if (weeks.Count >= 8 && nonRaceWeeks[^2] > peak * options.PreRaceWeekMaxShare + Tolerance)
            {
                violations.Add(new("pre_race_week_not_tapered", weeks[^2][0].WeekIndex, null, $"{nonRaceWeeks[^2]:0.#} km vs peak {peak:0.#} km"));
            }
        }

        return violations;
    }

    /// <summary>
    /// Checks an adapted plan against its previous version. Without <paramref name="allowIncrease"/> an adaptation may
    /// only redistribute or reduce load. With it (changes the athlete must approve), weekly volume and long runs may grow
    /// only within the normal progression limits, never in the taper, and the week must stay mostly easy.
    /// </summary>
    public IReadOnlyList<Violation> ValidateAdaptation(
        IReadOnlyList<PlannedWorkout> before, IReadOnlyList<PlannedWorkout> after, TrainingZones zones, bool allowIncrease)
    {
        var violations = new List<Violation>();
        CheckStructure(after, violations);
        CheckSpacing(after, violations);

        var beforeWeeks = before.GroupBy(w => w.WeekIndex).ToDictionary(g => g.Key, g => g.ToList());
        var afterWeeks = after.GroupBy(w => w.WeekIndex).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        foreach (var items in afterWeeks)
        {
            var weekIndex = items[0].WeekIndex;
            CheckQualityCount(weekIndex, items, violations);
            CheckLongRunCaps(weekIndex, LongKm(items), zones, violations);

            var km = WeekKm(items);
            var prev = beforeWeeks.TryGetValue(weekIndex, out var b) ? WeekKm(b) : km;
            var prevLong = b is null ? LongKm(items) : LongKm(b);
            var increased = km > prev + Tolerance || LongKm(items) > prevLong + Tolerance;
            // Same distance at a higher intensity (an easy run upgraded to intervals) is extra load too.
            var harder = b is not null && WeekHardKm(items, zones) > WeekHardKm(b, zones) + Tolerance;
            if (!increased && !harder)
            {
                continue;
            }

            if (!allowIncrease)
            {
                // Without approval only volume is checked: moving a session between weeks may shift intensity.
                if (increased)
                {
                    violations.Add(new("adaptation_increases_volume", weekIndex, null, $"{km:0.#} km vs {prev:0.#} km"));
                }

                continue;
            }

            if (items.Any(w => w.Phase == PlanPhase.Taper))
            {
                violations.Add(new("taper_increase", weekIndex, null, $"{km:0.#} km vs {prev:0.#} km"));
                continue;
            }

            if (items.Any(w => w.IsCutbackWeek))
            {
                // Recovery weeks keep their purpose: no extra load, whoever proposes it.
                violations.Add(new("cutback_increase", weekIndex, null, $"{km:0.#} km vs {prev:0.#} km"));
                continue;
            }

            // Reference: the closest earlier full training week (not cutback/taper), after the change.
            var reference = afterWeeks
                .Where(w => w[0].WeekIndex < weekIndex && !w.Any(x => x.IsCutbackWeek || x.Phase == PlanPhase.Taper))
                .LastOrDefault();
            var refKm = reference is null ? prev : WeekKm(reference);
            var refLong = reference is null ? prevLong : LongKm(reference);
            var limit = Math.Min(refKm * (1 + options.MaxWeeklyIncreasePct), refKm + options.MaxWeeklyIncreaseKm);
            if (km > Math.Max(limit, prev) + Tolerance)
            {
                violations.Add(new("adaptation_increase_too_large", weekIndex, null, $"{km:0.#} km vs limit {limit:0.#} km"));
            }

            if (LongKm(items) > Math.Max(refLong + options.LongRunMaxIncreaseKm, prevLong) + Tolerance)
            {
                violations.Add(new("long_run_increase_too_large", weekIndex, null, $"{LongKm(items):0.#} km after {refLong:0.#} km"));
            }

            CheckIntensityCaps(items, km, zones, violations);
            if (!items.Any(w => w.Type == WorkoutType.Race))
            {
                CheckEasyShare(weekIndex, items, km, zones, violations);
            }
        }

        return violations;
    }

    private static double WeekKm(IReadOnlyList<PlannedWorkout> items) =>
        items.Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm);

    private static double WeekHardKm(IReadOnlyList<PlannedWorkout> items, TrainingZones zones) =>
        items.Where(w => w.Type != WorkoutType.Race).Sum(w => WorkoutStructure.HardKm(w, zones));

    /// <summary>The longest non-race run of the week, whatever it is labelled.</summary>
    private static double LongKm(IReadOnlyList<PlannedWorkout> items) =>
        items.Where(w => w.Type is not (WorkoutType.Race or WorkoutType.Rest)).Select(w => w.TargetDistanceKm).DefaultIfEmpty(0).Max();

    private static void CheckStructure(IReadOnlyList<PlannedWorkout> workouts, List<Violation> violations)
    {
        foreach (var w in workouts)
        {
            if (w.Type != WorkoutType.Rest && w.TargetDistanceKm <= 0)
            {
                violations.Add(new("non_positive_distance", w.WeekIndex, w.Date, w.Type.ToString()));
            }
            else if (w.Type is not (WorkoutType.Rest or WorkoutType.Race) && w.TargetDistanceKm < MinRunKm - 0.01)
            {
                violations.Add(new("run_too_short", w.WeekIndex, w.Date, $"{w.TargetDistanceKm:0.#} km"));
            }
        }

        foreach (var dup in workouts.Where(w => w.Type != WorkoutType.Rest).GroupBy(w => w.Date).Where(g => g.Count() > 1))
        {
            violations.Add(new("multiple_workouts_same_day", dup.First().WeekIndex, dup.Key, $"{dup.Count()} workouts"));
        }
    }

    /// <summary>Structured sessions must be well-formed, match their type, and add up to their distance.</summary>
    private static void CheckSteps(IReadOnlyList<PlannedWorkout> workouts, TrainingZones zones, List<Violation> violations)
    {
        foreach (var w in workouts.Where(w => w.StepsJson is not null))
        {
            var blocks = WorkoutStructure.Parse(w.StepsJson);
            if (blocks is null)
            {
                violations.Add(new("steps_invalid", w.WeekIndex, w.Date, "unreadable steps"));
                continue;
            }

            var problems = WorkoutStructure.Problems(w.Type, blocks, zones.RaceIntensity);
            violations.AddRange(problems.Select(p => new Violation(p.Code, w.WeekIndex, w.Date, p.Detail)));
            if (problems.Count == 0 && blocks.Count > 0)
            {
                var total = WorkoutStructure.TotalKm(blocks, zones);
                if (Math.Abs(total - w.TargetDistanceKm) > Math.Max(1, 0.1 * w.TargetDistanceKm))
                {
                    violations.Add(new("steps_distance_mismatch", w.WeekIndex, w.Date, $"steps {total:0.#} km vs {w.TargetDistanceKm:0.#} km"));
                }
            }
        }
    }

    /// <summary>Per-session limits on time at each intensity, relative to the week's volume.</summary>
    private void CheckIntensityCaps(IReadOnlyList<PlannedWorkout> items, double weekKm, TrainingZones zones, List<Violation> violations)
    {
        foreach (var w in items.Where(w => w.Type is not (WorkoutType.Race or WorkoutType.Rest)))
        {
            foreach (var (band, km) in WorkoutStructure.BandKm(w, zones))
            {
                var limit = options.CapFor(band).LimitFor(weekKm);
                if (km > limit + 0.05)
                {
                    violations.Add(new($"too_much_{band.ToString().ToLowerInvariant()}", w.WeekIndex, w.Date,
                        $"{km:0.#} km at {band} intensity > {limit:0.#} km for a {weekKm:0.#} km week"));
                }
            }
        }
    }

    private static void CheckSpacing(IReadOnlyList<PlannedWorkout> workouts, List<Violation> violations)
    {
        var hard = workouts.Where(w => w.Type.IsHard()).OrderBy(w => w.Date).ToList();
        for (var i = 1; i < hard.Count; i++)
        {
            if (hard[i].Date.DayNumber - hard[i - 1].Date.DayNumber == 1)
            {
                violations.Add(new("hard_days_back_to_back", hard[i].WeekIndex, hard[i].Date,
                    $"{hard[i - 1].Type} then {hard[i].Type}"));
            }
        }
    }

    private static void CheckRace(IReadOnlyList<PlannedWorkout> workouts, PlanConstraints c, List<Violation> violations)
    {
        var races = workouts.Where(w => w.Type == WorkoutType.Race).ToList();
        if (races.Count != 1 || races[0].Date != c.RaceDate)
        {
            violations.Add(new("race_missing_or_wrong_date", null, c.RaceDate, $"{races.Count} race session(s)"));
        }

        foreach (var w in workouts.Where(w => w.Date > c.RaceDate && w.Type != WorkoutType.Rest))
        {
            violations.Add(new("session_after_race", w.WeekIndex, w.Date, w.Type.ToString()));
        }

        foreach (var w in workouts.Where(w => w.Type.IsHard() && w.Type != WorkoutType.Race && w.Date < c.RaceDate && w.Date >= c.RaceDate.AddDays(-2)))
        {
            violations.Add(new("hard_session_before_race", w.WeekIndex, w.Date, w.Type.ToString()));
        }
    }

    private static void CheckRunDays(IReadOnlyList<PlannedWorkout> workouts, PlanConstraints c, List<Violation> violations)
    {
        foreach (var week in workouts.Where(w => w.Type != WorkoutType.Rest).GroupBy(w => w.WeekIndex))
        {
            var days = week.Select(w => w.Date).Distinct().Count();
            if (days > c.RunDaysPerWeek)
            {
                violations.Add(new("too_many_run_days", week.Key, null, $"{days} > {c.RunDaysPerWeek}"));
            }
        }
    }

    private void CheckQualityCount(int weekIndex, IReadOnlyList<PlannedWorkout> items, List<Violation> violations)
    {
        var count = items.Count(w => w.Type.IsQuality());
        var max = items.Any(w => w.Phase == PlanPhase.Base) ? options.MaxQualityPerWeekBase : options.MaxQualityPerWeek;
        if (count > max)
        {
            violations.Add(new("too_many_quality_sessions", weekIndex, null, $"{count} > {max}"));
        }
    }

    private void CheckLongRunCaps(int weekIndex, double longKm, TrainingZones zones, List<Violation> violations)
    {
        if (longKm > options.LongRunCapKm + Tolerance)
        {
            violations.Add(new("long_run_over_cap", weekIndex, null, $"{longKm:0.#} km > {options.LongRunCapKm} km"));
        }

        var minutes = longKm * zones.SlowestPaceFor(WorkoutType.Long) / 60.0;
        if (minutes > options.LongRunCapMinutes + 1)
        {
            violations.Add(new("long_run_over_duration_cap", weekIndex, null, $"~{minutes:0} min > {options.LongRunCapMinutes} min"));
        }
    }

    private void CheckEasyShare(int weekIndex, IReadOnlyList<PlannedWorkout> items, double km, TrainingZones zones, List<Violation> violations)
    {
        if (km <= 0)
        {
            return;
        }

        var hardKm = WeekHardKm(items, zones);
        if (hardKm / km > 1 - options.MinEasyShare + 0.005)
        {
            violations.Add(new("too_little_easy_running", weekIndex, null, $"{hardKm / km:P0} hard"));
        }
    }
}
