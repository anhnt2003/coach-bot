using CoachBot.Core.Domain;
using CoachBot.Core.Planning;

namespace CoachBot.Core.Analysis;

public static class ComplianceStatus
{
    public const string OnTarget = "on_target";
    public const string Faster = "faster";
    public const string Slower = "slower";
    public const string Above = "above";
    public const string Below = "below";
    public const string Unknown = "unknown";
}

public sealed record RunCompliance(double? DistanceRatio, string PaceStatus, string HrStatus, IReadOnlyList<string> Flags);

public static class RunAnalyzer
{
    private const double Tolerance = 0.03;

    /// <summary>
    /// Compares a completed run with its planned workout. Average pace/HR are only judged for steady sessions:
    /// for intervals and tempo runs the average includes warm-up and recoveries, so it would be misleading.
    /// </summary>
    public static RunCompliance Analyze(Activity activity, PlannedWorkout? planned)
    {
        if (planned is null)
        {
            return new(null, ComplianceStatus.Unknown, ComplianceStatus.Unknown, ["unplanned_run"]);
        }

        var flags = new List<string>();
        double? ratio = planned.TargetDistanceKm > 0 ? Math.Round(activity.DistanceKm / planned.TargetDistanceKm, 2) : null;
        if (ratio < 0.8)
        {
            flags.Add(planned.Type == WorkoutType.Long ? "long_cut_short" : "cut_short");
        }
        else if (ratio > 1.2)
        {
            flags.Add("ran_longer");
        }

        // A long run with a race-pace finish is not steady: its average pace says nothing about the easy part.
        var steady = WorkoutStructure.IsSteadyEasy(planned);
        var pace = ComplianceStatus.Unknown;
        if (steady && activity.AvgPaceSecPerKm is int p && planned.PaceMinSecPerKm is int fast && planned.PaceMaxSecPerKm is int slow)
        {
            pace = p < fast * (1 - Tolerance) ? ComplianceStatus.Faster
                : p > slow * (1 + Tolerance) ? ComplianceStatus.Slower
                : ComplianceStatus.OnTarget;
            if (pace == ComplianceStatus.Faster)
            {
                flags.Add("easy_too_fast");
            }
        }

        var hr = ComplianceStatus.Unknown;
        if (steady && activity.AvgHr is int avgHr && planned.HrMin is int lo && planned.HrMax is int hi)
        {
            hr = avgHr > hi * (1 + Tolerance) ? ComplianceStatus.Above
                : avgHr < lo * (1 - Tolerance) ? ComplianceStatus.Below
                : ComplianceStatus.OnTarget;
            if (hr == ComplianceStatus.Above)
            {
                flags.Add("hr_above_zone");
            }
        }

        return new(ratio, pace, hr, flags);
    }
}

public static class WorkoutMatcher
{
    /// <summary>
    /// Finds the planned workout a run most likely fulfils: same day first, then ±1 day, closest distance wins.
    /// </summary>
    public static PlannedWorkout? Match(DateOnly runDate, double runKm, IEnumerable<PlannedWorkout> candidates)
    {
        return candidates
            .Where(w => w.Status == WorkoutStatus.Planned && w.Type != WorkoutType.Rest)
            .Where(w => Math.Abs(w.Date.DayNumber - runDate.DayNumber) <= 1)
            .OrderBy(w => Math.Abs(w.Date.DayNumber - runDate.DayNumber))
            .ThenBy(w => Math.Abs(w.TargetDistanceKm - runKm))
            .FirstOrDefault();
    }
}
