using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;

namespace CoachBot.Core.Planning;

public sealed record PlanInputs(
    DateOnly Today,
    DateOnly RaceDate,
    double RaceDistanceKm,
    double RecentWeeklyKmAvg,
    double LongestRecentRunKm,
    int WeeksOfHistory,
    int RunDaysPerWeek,
    DayOfWeek LongRunDay,
    TrainingZones Zones);

/// <summary>Reference volumes the first plan week is checked against.</summary>
public sealed record PlanBaseline(double WeeklyKm, double LongRunKm);

public sealed record PlanDraft(
    bool Feasible,
    IReadOnlyList<string> Warnings,
    DateOnly StartDate,
    DateOnly EndDate,
    PlanBaseline Baseline,
    IReadOnlyList<PlannedWorkout> Workouts);

/// <summary>
/// Builds a periodized plan (base → build → peak → taper) from current fitness using fixed, conservative rules.
/// The output contains numbers only; titles/descriptions are filled in by the caller (templates or LLM).
/// Every plan it produces must pass <see cref="Safety.PlanValidator"/> (enforced by tests). Pass options already
/// adjusted for the goal distance (<see cref="SafetyOptions.ForRace"/>).
/// </summary>
public sealed class PlanGenerator(SafetyOptions options)
{
    private const double MinRunKm = 3;
    // Below this an easy run is dropped (rest day) so the remaining easy runs are worth lacing up for.
    private const double MinEasyRunKm = 5;

    private enum Slot { Long, Quality1, Quality2, Easy }

    // Weekly layouts for a Sunday long run, indexed Monday=0. Rotated for other long-run days.
    // Hard sessions are always separated by at least one easy/rest day, including across weeks.
    private static readonly Dictionary<int, (int Day, Slot Slot)[]> Layouts = new()
    {
        [3] = [(1, Slot.Quality1), (3, Slot.Easy), (6, Slot.Long)],
        [4] = [(1, Slot.Quality1), (3, Slot.Quality2), (5, Slot.Easy), (6, Slot.Long)],
        [5] = [(1, Slot.Quality1), (2, Slot.Easy), (3, Slot.Quality2), (5, Slot.Easy), (6, Slot.Long)],
        [6] = [(0, Slot.Easy), (1, Slot.Quality1), (2, Slot.Easy), (3, Slot.Quality2), (5, Slot.Easy), (6, Slot.Long)],
    };

    public PlanDraft Generate(PlanInputs input)
    {
        var warnings = new List<string>();
        var start = MondayOnOrAfter(input.Today.AddDays(1));
        var raceWeekStart = MondayOf(input.RaceDate);
        var totalWeeks = raceWeekStart < start ? 0 : (raceWeekStart.DayNumber - start.DayNumber) / 7 + 1;

        var hasHistory = input.WeeksOfHistory >= 4;
        if (!hasHistory)
        {
            warnings.Add("plan.warn_little_history");
        }

        var baseKm = input.RecentWeeklyKmAvg;
        if (baseKm < 1)
        {
            baseKm = 15;
            warnings.Add("plan.warn_default_volume");
        }

        var startLong = input.LongestRecentRunKm > 0 ? input.LongestRecentRunKm : Math.Min(10, baseKm * 0.35);
        var baseline = new PlanBaseline(baseKm, startLong);

        PlanDraft Infeasible(string reason) =>
            new(false, [.. warnings, reason], start, input.RaceDate, baseline, []);

        if (totalWeeks < 1)
        {
            return Infeasible("plan.infeasible_race_too_soon");
        }

        var experienced = baseKm >= options.MinBaseKmForShortBuild && input.LongestRecentRunKm >= options.MinLongRunKmForShortBuild;
        if (totalWeeks < options.MinWeeksAbsolute && !experienced)
        {
            return Infeasible("plan.infeasible_too_short");
        }

        // Below ~10 km/week the first plan week would already be a large jump: build a base first.
        if (hasHistory && input.RecentWeeklyKmAvg < 10)
        {
            return Infeasible("plan.infeasible_low_base");
        }

        if (totalWeeks < options.MinWeeksRecommended)
        {
            warnings.Add("plan.warn_short");
        }

        if (baseKm < options.LowBaseWarningKm)
        {
            warnings.Add("plan.warn_low_base");
        }

        var days = Math.Clamp(input.RunDaysPerWeek, 3, 6);
        var taperWeeks = Math.Min(totalWeeks >= 12 ? 3 : totalWeeks >= 8 ? 2 : 1, options.TaperFactors.Length);
        taperWeeks = Math.Min(taperWeeks, totalWeeks);
        var buildWeeks = totalWeeks - taperWeeks;
        var taperFactors = options.TaperFactors[^taperWeeks..];
        var phases = AssignBuildPhases(buildWeeks);

        var peakCap = Math.Max(baseKm, Math.Min(options.PeakVolumeCapKm, Math.Max(options.PeakFloorKm, baseKm * options.PeakBaseMultiplier)));
        var shortRace = RaceProfiles.For(input.RaceDistanceKm) is RaceProfile.FiveK or RaceProfile.TenK;
        var workouts = new List<PlannedWorkout>();
        var lastFullKm = baseKm;
        var lastFullLong = startLong;
        var peakKm = 0.0;
        var peakLong = 0.0;

        for (var i = 0; i < buildWeeks; i++)
        {
            var cutback = options.CutbackEveryWeeks > 0 && (i + 1) % options.CutbackEveryWeeks == 0 && i != buildWeeks - 1;
            double targetKm;
            if (i == 0)
            {
                targetKm = Math.Min(baseKm, peakCap);
            }
            else if (cutback)
            {
                targetKm = lastFullKm * options.CutbackFactor;
            }
            else
            {
                targetKm = Math.Min(
                    Math.Min(lastFullKm * (1 + options.MaxWeeklyIncreasePct), lastFullKm + options.MaxWeeklyIncreaseKm),
                    peakCap);
            }

            targetKm = Floor05(Math.Max(targetKm, 10));

            var longKm = cutback
                ? lastFullLong * options.CutbackFactor
                : Math.Min(i == 0 ? lastFullLong : lastFullLong + options.LongRunMaxIncreaseKm, LongRunCap(targetKm, input));
            longKm = Floor05(Math.Max(longKm, 5));

            var weekStart = start.AddDays(7 * i);
            var phase = phases[i];
            var qualities = cutback ? QualitiesFor(phase, shortRace).Take(1).ToArray() : QualitiesFor(phase, shortRace);
            var week = BuildWeek(weekStart, i, phase, cutback, targetKm, longKm, qualities, days, input);
            workouts.AddRange(week);

            var actualKm = week.Sum(w => w.TargetDistanceKm);
            var actualLong = week.Where(w => w.Type == WorkoutType.Long).Select(w => w.TargetDistanceKm).DefaultIfEmpty(0).Max();
            if (!cutback)
            {
                lastFullKm = actualKm;
                lastFullLong = actualLong;
            }

            peakKm = Math.Max(peakKm, actualKm);
            peakLong = Math.Max(peakLong, actualLong);
        }

        if (buildWeeks == 0)
        {
            peakKm = baseKm;
            peakLong = startLong;
        }

        var prevTaperKm = double.MaxValue;
        for (var t = 0; t < taperWeeks; t++)
        {
            var weekIndex = buildWeeks + t;
            var weekStart = start.AddDays(7 * weekIndex);
            var factor = taperFactors[t];
            var isRaceWeek = t == taperWeeks - 1;
            // Taper volume only ever steps down, even when rounding left the previous week below its target. The 8 km
            // floor keeps a few short runs, but never above the race-week share of a small peak.
            var floorKm = isRaceWeek ? Math.Min(8, peakKm * options.RaceWeekMaxShare) : 8;
            var targetKm = Floor05(Math.Min(Math.Max(peakKm * factor, floorKm), prevTaperKm));

            var week = isRaceWeek
                ? BuildRaceWeek(weekStart, weekIndex, targetKm, days, input)
                : BuildWeek(weekStart, weekIndex, PlanPhase.Taper, false, targetKm,
                    Floor05(Math.Max(Math.Min(peakLong * factor, LongRunCap(targetKm, input)), 5)),
                    QualitiesFor(PlanPhase.Taper, shortRace), days, input);
            workouts.AddRange(week);
            prevTaperKm = week.Where(w => w.Type != WorkoutType.Race).Sum(w => w.TargetDistanceKm);
        }

        // The two days before the race are never hard (matters when the race falls early in the week,
        // right after the previous week's long run).
        var shortened = new HashSet<int>();
        foreach (var w in workouts.Where(w => w.Type.IsHard() && w.Type != WorkoutType.Race
                     && w.Date >= input.RaceDate.AddDays(-2) && w.Date < input.RaceDate).ToList())
        {
            var replacement = NewWorkout(w.Date, w.WeekIndex, w.Phase, w.IsCutbackWeek, WorkoutType.Easy,
                Math.Min(w.TargetDistanceKm, 6), input.Zones);
            workouts[workouts.IndexOf(w)] = replacement;
            shortened.Add(w.WeekIndex);
        }

        foreach (var weekIndex in shortened)
        {
            Rebalance(workouts, weekIndex, input.Zones);
        }

        return new PlanDraft(true, warnings, start, input.RaceDate, baseline, workouts);
    }

    /// <summary>
    /// Restores the weekly limits after a week lost volume (pre-race easy days): quality sessions shrink to their
    /// intensity limits, the last one becomes easy if the week is still too hard, and the longest run shrinks to its share.
    /// </summary>
    private void Rebalance(List<PlannedWorkout> workouts, int weekIndex, TrainingZones zones)
    {
        void Replace(PlannedWorkout w, WorkoutType type, double km) =>
            workouts[workouts.IndexOf(w)] = NewWorkout(w.Date, w.WeekIndex, w.Phase, w.IsCutbackWeek, type, km, zones);

        for (var pass = 0; pass < 20; pass++)
        {
            var week = workouts.Where(w => w.WeekIndex == weekIndex && w.Type is not (WorkoutType.Race or WorkoutType.Rest)).ToList();
            var total = week.Sum(w => w.TargetDistanceKm);
            var quality = week.Where(w => w.Type.IsQuality()).ToList();

            var tooIntense = quality.FirstOrDefault(q =>
                q.TargetDistanceKm * q.Type.HardShare() > options.CapFor(WorkoutStructure.DefaultBand(q.Type, zones)).LimitFor(total) + 0.01);
            if (tooIntense is not null)
            {
                var maxKm = options.CapFor(WorkoutStructure.DefaultBand(tooIntense.Type, zones)).LimitFor(total) / tooIntense.Type.HardShare();
                Replace(tooIntense, tooIntense.Type, Math.Max(Floor05(maxKm), MinRunKm));
                continue;
            }

            if (quality.Count > 0 && week.Sum(w => w.Type.HardShare() * w.TargetDistanceKm) > (1 - options.MinEasyShare) * total)
            {
                Replace(quality[^1], WorkoutType.Easy, quality[^1].TargetDistanceKm);
                continue;
            }

            var longest = week.MaxBy(w => w.TargetDistanceKm);
            var maxLong = Floor05((total - (longest?.TargetDistanceKm ?? 0)) * options.LongRunHardMaxShare / (1 - options.LongRunHardMaxShare));
            if (longest is not null && longest.Phase != PlanPhase.Taper && longest.TargetDistanceKm > maxLong && maxLong >= MinRunKm)
            {
                Replace(longest, longest.Type, maxLong);
                continue;
            }

            return;
        }
    }

    private double LongRunCap(double weekKm, PlanInputs input)
    {
        // Keep an already-established long run (up to the hard share limit) rather than cutting it back to the target share.
        var shareCap = Math.Max(options.LongRunTargetShare * weekKm, Math.Min(input.LongestRecentRunKm, options.LongRunHardMaxShare * weekKm));
        var minutesCap = options.LongRunCapMinutes * 60.0 / input.Zones.SlowestPaceFor(WorkoutType.Long);
        return Math.Min(Math.Min(shareCap, options.LongRunHardMaxShare * weekKm), Math.Min(options.LongRunCapKm, minutesCap));
    }

    // Race-specific work: goal-pace sessions for the half and the marathon; for 5K/10K the intervals already run near
    // race pace, so the rule-based plan keeps intervals and tempo (the LLM designs finer race-pace work).
    private static WorkoutType[] QualitiesFor(PlanPhase phase, bool shortRace) => phase switch
    {
        PlanPhase.Base => [WorkoutType.Tempo],
        PlanPhase.Build => [WorkoutType.Interval, WorkoutType.Tempo],
        PlanPhase.Peak => shortRace ? [WorkoutType.Interval, WorkoutType.Tempo] : [WorkoutType.Tempo, WorkoutType.RacePace],
        _ => shortRace ? [WorkoutType.Interval] : [WorkoutType.RacePace],
    };

    private List<PlannedWorkout> BuildWeek(
        DateOnly weekStart, int weekIndex, PlanPhase phase, bool cutback, double targetKm, double longKm,
        WorkoutType[] qualityTypes, int days, PlanInputs input)
    {
        var maxQuality = phase == PlanPhase.Base ? options.MaxQualityPerWeekBase : options.MaxQualityPerWeek;
        if (days < 4)
        {
            maxQuality = Math.Min(maxQuality, 1);
        }

        if (targetKm < 20)
        {
            maxQuality = 0;
        }

        var qualities = qualityTypes.Take(maxQuality).ToList();

        while (true)
        {
            var layout = Layouts[days]
                .Select(s => s.Slot switch
                {
                    Slot.Quality1 when qualities.Count >= 1 => (s.Day, Type: qualities[0]),
                    Slot.Quality2 when qualities.Count >= 2 => (s.Day, Type: qualities[1]),
                    Slot.Long => (s.Day, Type: WorkoutType.Long),
                    _ => (s.Day, Type: WorkoutType.Easy),
                })
                .ToList();

            var distances = Distribute(layout.Select(l => l.Type).ToList(), targetKm, longKm);
            var hardKm = layout.Select((l, idx) => l.Type.HardShare() * distances[idx]).Sum();
            var total = distances.Sum();
            var hasEasySlot = layout.Any(l => l.Type == WorkoutType.Easy);
            var easyKm = layout.Select((l, idx) => l.Type == WorkoutType.Easy ? distances[idx] : 0).Sum();

            // Too much intensity, or quality sessions crowding out every easy run: drop the last quality session.
            if (qualities.Count > 0 && total > 0 && (hardKm / total > 1 - options.MinEasyShare || (hasEasySlot && easyKm < MinEasyRunKm)))
            {
                qualities.RemoveAt(qualities.Count - 1);
                continue;
            }

            // Both the long-run share and the per-session intensity limits are relative to the week's actual total, which
            // can end up below target after rounding/dropping short runs. Shrinking one lowers the total, so repeat.
            var longIdx = layout.FindIndex(l => l.Type == WorkoutType.Long);
            for (var pass = 0; pass < 10; pass++)
            {
                var changed = false;
                for (var i = 0; i < layout.Count; i++)
                {
                    var band = WorkoutStructure.DefaultBand(layout[i].Type, input.Zones);
                    if (band == IntensityBand.None)
                    {
                        continue;
                    }

                    var maxKm = options.CapFor(band).LimitFor(distances.Sum()) / layout[i].Type.HardShare();
                    if (distances[i] > maxKm + 0.01)
                    {
                        distances[i] = Math.Max(Floor05(maxKm), MinRunKm);
                        changed = true;
                    }
                }

                // L <= s(T - L + L')  =>  L' <= s/(1-s) * (others).
                var others = distances.Sum() - distances[longIdx];
                var share = options.LongRunHardMaxShare;
                var maxLong = Math.Max(Floor05(others * share / (1 - share)), MinRunKm);
                if (distances[longIdx] > maxLong)
                {
                    distances[longIdx] = maxLong;
                    changed = true;
                }

                if (!changed)
                {
                    break;
                }
            }

            return Materialize(layout, distances, weekStart, weekIndex, phase, cutback, input);
        }
    }

    /// <summary>Splits the weekly target over the layout without ever exceeding it.</summary>
    private static List<double> Distribute(List<WorkoutType> types, double targetKm, double longKm)
    {
        var distances = new double[types.Count];
        var qualitySum = 0.0;
        for (var i = 0; i < types.Count; i++)
        {
            distances[i] = types[i] switch
            {
                WorkoutType.Long => longKm,
                WorkoutType.Tempo => Floor05(Math.Min(Math.Clamp(0.18 * targetKm, 6, 14), longKm)),
                WorkoutType.Interval => Floor05(Math.Min(Math.Clamp(0.16 * targetKm, 6, 12), longKm)),
                WorkoutType.RacePace => Floor05(Math.Min(Math.Clamp(0.20 * targetKm, 6, 16), longKm)),
                _ => 0,
            };
            if (types[i].IsQuality())
            {
                qualitySum += distances[i];
            }
        }

        var easyIdx = Enumerable.Range(0, types.Count).Where(i => types[i] == WorkoutType.Easy).ToList();
        var remaining = targetKm - longKm - qualitySum;

        // Drop easy runs (to rest) until each remaining easy run is a meaningful distance.
        while (easyIdx.Count > 0 && remaining / easyIdx.Count < MinEasyRunKm)
        {
            easyIdx.RemoveAt(easyIdx.Count - 1);
        }

        var perEasy = easyIdx.Count > 0 ? Floor05(Math.Min(remaining / easyIdx.Count, longKm)) : 0;
        foreach (var i in easyIdx)
        {
            distances[i] = perEasy;
        }

        // If long + quality alone exceed the target, shrink quality sessions (never below MinRunKm).
        var overflow = distances.Sum() - targetKm;
        for (var i = 0; i < types.Count && overflow > 0; i++)
        {
            if (types[i].IsQuality())
            {
                var cut = Math.Min(overflow, distances[i] - MinRunKm);
                cut = Math.Ceiling(Math.Max(cut, 0) * 2) / 2;
                distances[i] -= cut;
                overflow -= cut;
            }
        }

        return [.. distances];
    }

    private List<PlannedWorkout> Materialize(
        List<(int Day, WorkoutType Type)> layout, List<double> distances, DateOnly weekStart, int weekIndex,
        PlanPhase phase, bool cutback, PlanInputs input)
    {
        var shift = MondayIndex(input.LongRunDay) - 6;
        var result = new List<PlannedWorkout>();
        var hardDays = layout.Where(l => l.Type.IsHard()).Select(l => l.Day).ToHashSet();

        for (var i = 0; i < layout.Count; i++)
        {
            if (distances[i] < MinRunKm && layout[i].Type != WorkoutType.Long)
            {
                continue;
            }

            var type = layout[i].Type;
            if (type == WorkoutType.Easy && hardDays.Contains((layout[i].Day + 6) % 7))
            {
                type = WorkoutType.Recovery;
            }

            var date = weekStart.AddDays((layout[i].Day + shift + 7) % 7);
            result.Add(NewWorkout(date, weekIndex, phase, cutback, type, distances[i], input.Zones));
        }

        return [.. result.OrderBy(w => w.Date)];
    }

    private List<PlannedWorkout> BuildRaceWeek(DateOnly weekStart, int weekIndex, double targetKm, int days, PlanInputs input)
    {
        var shift = MondayIndex(input.LongRunDay) - 6;
        var runDates = Layouts[days]
            .Select(s => weekStart.AddDays((s.Day + shift + 7) % 7))
            .Where(d => d < input.RaceDate.AddDays(-1))
            .OrderBy(d => d)
            .ToList();

        var result = new List<PlannedWorkout>();
        if (runDates.Count > 0)
        {
            var perRun = Floor05(Math.Min(targetKm / runDates.Count, 8));
            if (perRun >= MinRunKm)
            {
                result.AddRange(runDates.Select(d => NewWorkout(d, weekIndex, PlanPhase.Taper, false, WorkoutType.Easy, perRun, input.Zones)));
            }
        }

        result.Add(NewWorkout(input.RaceDate, weekIndex, PlanPhase.Taper, false, WorkoutType.Race, input.RaceDistanceKm, input.Zones));
        return result;
    }

    private static PlannedWorkout NewWorkout(DateOnly date, int weekIndex, PlanPhase phase, bool cutback, WorkoutType type, double km, TrainingZones zones)
    {
        var pace = zones.PaceFor(type);
        var hr = zones.HrFor(type);
        return new PlannedWorkout
        {
            Date = date,
            WeekIndex = weekIndex,
            Phase = phase,
            IsCutbackWeek = cutback,
            Type = type,
            TargetDistanceKm = km,
            PaceMinSecPerKm = pace?.Fast,
            PaceMaxSecPerKm = pace?.Slow,
            HrMin = hr?.Min,
            HrMax = hr?.Max,
            TargetDurationMinutes = pace is null ? null : (int)Math.Round(km * (pace.Fast + pace.Slow) / 2.0 / 60.0),
            Status = WorkoutStatus.Planned,
            CorosSyncStatus = SyncStatus.NotSynced,
        };
    }

    private static PlanPhase[] AssignBuildPhases(int buildWeeks)
    {
        var baseN = (int)Math.Round(buildWeeks * 0.4);
        var peakN = buildWeeks >= 3 ? Math.Max(1, (int)Math.Round(buildWeeks * 0.2)) : 0;
        var buildN = buildWeeks - baseN - peakN;
        return [.. Enumerable.Repeat(PlanPhase.Base, baseN), .. Enumerable.Repeat(PlanPhase.Build, buildN), .. Enumerable.Repeat(PlanPhase.Peak, peakN)];
    }

    internal static double Floor05(double km) => Math.Floor(km * 2) / 2;

    internal static int MondayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    internal static DateOnly MondayOf(DateOnly date) => date.AddDays(-MondayIndex(date.DayOfWeek));

    private static DateOnly MondayOnOrAfter(DateOnly date) => date.DayOfWeek == DayOfWeek.Monday ? date : MondayOf(date).AddDays(7);
}
