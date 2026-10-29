using System.Globalization;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;

namespace CoachBot.Core.Services;

/// <summary>Renders workouts and numbers into localized text. All user-facing strings come from <see cref="ITextCatalog"/>.</summary>
public sealed class MessageFormatter(ITextCatalog text)
{
    public string T(string lang, string key, params object?[] args) => text.Get(lang, key, args);

    public static string Pace(int? secPerKm) => secPerKm is int s ? $"{s / 60}:{s % 60:00}" : "–";

    public static string Km(double km) => km.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>"1:45:30", or "22:30" under an hour.</summary>
    public static string Time(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>"Half marathon", "10K"…, or "15 km" for a non-standard distance.</summary>
    public string RaceDistance(string lang, double km)
    {
        foreach (var (standard, key) in new[] { (RaceProfiles.FiveKKm, "5k"), (RaceProfiles.TenKKm, "10k"), (RaceProfiles.HalfKm, "half"), (RaceProfiles.MarathonKm, "marathon") })
        {
            if (Math.Abs(km - standard) < 0.15)
            {
                return T(lang, $"race.distance.{key}");
            }
        }

        return $"{Km(km)} km";
    }

    public static CultureInfo Culture(string lang) => CultureInfo.GetCultureInfo(lang == "vi" ? "vi-VN" : "en-GB");

    public string Day(string lang, DateOnly date) => date.ToString("ddd dd/MM", Culture(lang));

    public string TypeName(string lang, WorkoutType type) => T(lang, $"workout.type.{type.ToString().ToLowerInvariant()}");

    public string Title(string lang, WorkoutType type, double km) =>
        type == WorkoutType.Rest ? TypeName(lang, type) : $"{TypeName(lang, type)} {Km(km)} km";

    /// <summary>Session structure plus targets (pace/HR, or RPE when no physiological data exists).</summary>
    public string Description(string lang, PlannedWorkout w)
    {
        if (WorkoutStructure.Parse(w.StepsJson) is { Count: > 0 } blocks)
        {
            // Paces are part of the steps; add heart rate, or effort when there is no pace or HR data at all.
            var extra = w.HrMin is not null ? T(lang, "workout.target.hr", w.HrMin, w.HrMax)
                : w.PaceMinSecPerKm is null && blocks.All(b => b.Work.PaceFast is null) ? T(lang, $"workout.rpe.{w.Type.ToString().ToLowerInvariant()}")
                : null;
            return extra is null ? Steps(lang, blocks) : $"{Steps(lang, blocks)} · {extra}";
        }

        var km = w.TargetDistanceKm;
        var structure = w.Type switch
        {
            WorkoutType.Tempo => T(lang, "workout.desc.tempo", Km(Math.Max(km - 4, km * 0.5))),
            WorkoutType.Interval => T(lang, "workout.desc.interval", Math.Max(3, (int)Math.Round(km * 0.4))),
            WorkoutType.RacePace => T(lang, "workout.desc.race_pace", Km(PlanCalendar.Floor05(km * 0.6))),
            WorkoutType.Long => T(lang, "workout.desc.long"),
            WorkoutType.Recovery => T(lang, "workout.desc.recovery"),
            WorkoutType.Easy => T(lang, "workout.desc.easy"),
            WorkoutType.Race => T(lang, "workout.desc.race"),
            _ => T(lang, "workout.desc.rest"),
        };

        return w.Type == WorkoutType.Rest ? structure : $"{structure} {Targets(lang, w)}";
    }

    /// <summary>E.g. "2 km easy → 6 × (1 km 5K pace 4:05–4:15/km, 2 min jog) → 2 km easy".</summary>
    public string Steps(string lang, IReadOnlyList<WorkoutBlock> blocks) =>
        string.Join(" → ", blocks.Select(b =>
        {
            var work = Part(lang, b.Work);
            var body = b.Rest is null ? work : $"{work}, {Part(lang, b.Rest)}";
            return b.Repeat == 1 ? body : b.Rest is null ? $"{b.Repeat} × {body}" : $"{b.Repeat} × ({body})";
        }));

    private string Part(string lang, StepPart p)
    {
        var amount = p.Km is double km
            ? km < 1 ? $"{Math.Round(km * 1000)} m" : $"{Km(km)} km"
            : p.Minutes is double min && (min < 1 || (min < 3 && Math.Abs(min - Math.Round(min)) > 0.01))
                ? $"{Math.Round(min * 60)} s"
                : $"{Km(p.Minutes ?? 0)} min";
        var label = T(lang, $"intensity.{PlanDesignMapper.IntensityName(p.Intensity)}");
        var showPace = p.PaceFast is not null && p.Intensity is not (Intensity.Easy or Intensity.Recovery);
        return showPace ? $"{amount} {label} {Pace(p.PaceFast)}–{Pace(p.PaceSlow)}/km" : $"{amount} {label}";
    }

    public string Targets(string lang, PlannedWorkout w) => Targets(lang, w.Type, w.PaceMinSecPerKm, w.PaceMaxSecPerKm, w.HrMin, w.HrMax);

    private string Targets(string lang, WorkoutType type, int? paceMin, int? paceMax, int? hrMin, int? hrMax)
    {
        var parts = new List<string>();
        if (paceMin is not null)
        {
            parts.Add(T(lang, "workout.target.pace", Pace(paceMin), Pace(paceMax)));
        }

        if (hrMin is not null)
        {
            parts.Add(T(lang, "workout.target.hr", hrMin, hrMax));
        }

        if (parts.Count == 0)
        {
            parts.Add(T(lang, $"workout.rpe.{type.ToString().ToLowerInvariant()}"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Sets the athlete-facing texts of a workout the LLM designed or changed: its title and note go in front of the
    /// templated structure and targets.
    /// </summary>
    public void ComposeTexts(string lang, PlannedWorkout w, string? title, string? notes)
    {
        if (w.Type == WorkoutType.Rest)
        {
            w.Title = TypeName(lang, w.Type);
            w.Description = string.IsNullOrWhiteSpace(notes) ? T(lang, "workout.desc.rest") : notes.Trim();
            return;
        }

        var templated = Description(lang, w);
        w.Title = string.IsNullOrWhiteSpace(title) ? Title(lang, w.Type, w.TargetDistanceKm) : $"{title.Trim()} · {Km(w.TargetDistanceKm)} km";
        w.Description = string.IsNullOrWhiteSpace(notes) ? templated
            : w.StepsJson is null ? $"{notes.Trim()} {Targets(lang, w)}"
            : $"{notes.Trim()}\n{templated}";
    }

    /// <summary>One line per workout, e.g. "Tue 21/10 · Tempo 10 km · 5:23–5:40/km".</summary>
    public string Line(string lang, PlannedWorkout w)
    {
        var status = w.Status switch
        {
            WorkoutStatus.Completed => " ✅",
            WorkoutStatus.Missed => " ❌",
            WorkoutStatus.Skipped => " ⏭",
            _ => "",
        };

        return Line(lang, PlanAdjuster.State(w)) + status;
    }

    public string Line(string lang, WorkoutState s)
    {
        var title = string.IsNullOrEmpty(s.Title) ? Title(lang, s.Type, s.Km) : s.Title;
        return s.Type == WorkoutType.Rest
            ? $"{Day(lang, s.Date)} · {title}"
            : $"{Day(lang, s.Date)} · {title} · {Targets(lang, s.Type, s.PaceMin, s.PaceMax, s.HrMin, s.HrMax)}";
    }

    /// <summary>"Tue 27/10 · Tempo 8 km · …\n   → Tue 27/10 · Easy run 6 km · … (reason)".</summary>
    public string ChangeLine(string lang, WorkoutChange c) =>
        T(lang, "change.line", Line(lang, c.Before), Line(lang, c.After)) + (string.IsNullOrWhiteSpace(c.Reason) ? "" : T(lang, "change.reason", c.Reason.Trim()));

    /// <summary>"🟠 Readiness: … (confidence: medium)".</summary>
    public string ReadinessLine(string lang, ReadinessLevel level, Confidence confidence) =>
        T(lang, $"readiness.level.{level.ToString().ToLowerInvariant()}") + " " + T(lang, $"readiness.confidence.{confidence.ToString().ToLowerInvariant()}");
}
