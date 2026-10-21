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
            WorkoutType.RacePace => T(lang, "workout.desc.race_pace", Km(PlanGenerator.Floor05(km * 0.6))),
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

    public string Targets(string lang, PlannedWorkout w)
    {
        var parts = new List<string>();
        if (w.PaceMinSecPerKm is not null)
        {
            parts.Add(T(lang, "workout.target.pace", Pace(w.PaceMinSecPerKm), Pace(w.PaceMaxSecPerKm)));
        }

        if (w.HrMin is not null)
        {
            parts.Add(T(lang, "workout.target.hr", w.HrMin, w.HrMax));
        }

        if (parts.Count == 0)
        {
            parts.Add(T(lang, $"workout.rpe.{w.Type.ToString().ToLowerInvariant()}"));
        }

        return string.Join(" · ", parts);
    }

    public void FillTexts(string lang, PlannedWorkout w)
    {
        w.Title = Title(lang, w.Type, w.TargetDistanceKm);
        w.Description = Description(lang, w);
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

        var title = string.IsNullOrEmpty(w.Title) ? Title(lang, w.Type, w.TargetDistanceKm) : w.Title;
        return w.Type == WorkoutType.Rest
            ? $"{Day(lang, w.Date)} · {title}{status}"
            : $"{Day(lang, w.Date)} · {title} · {Targets(lang, w)}{status}";
    }

    public string ChangeLine(string lang, ProposedChange c) => c.Action switch
    {
        ChangeAction.ConvertToRest => T(lang, "change.to_rest", Day(lang, c.Date), Title(lang, c.FromType, c.FromKm)),
        ChangeAction.SwapDays => T(lang, "change.swap", Day(lang, c.Date), Day(lang, c.NewDate ?? c.Date), Title(lang, c.FromType, c.FromKm)),
        ChangeAction.ChangePace => T(lang, "change.pace", Day(lang, c.Date), Title(lang, c.FromType, c.FromKm), Pace(c.NewPaceMinSecPerKm), Pace(c.NewPaceMaxSecPerKm)),
        _ => T(lang, "change.modify", Day(lang, c.Date), Title(lang, c.FromType, c.FromKm), Title(lang, c.ToType, c.ToKm)),
    };

    public string SignalLine(string lang, Safety.ReadinessSignal s)
    {
        var icon = s.Level switch { ReadinessLevel.Red => "🔴", ReadinessLevel.Amber => "🟠", _ => "🟢" };
        var value = s.Value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "–";
        var baseline = s.Baseline is double b ? T(lang, "readiness.baseline", b.ToString("0.#", CultureInfo.InvariantCulture)) : "";
        return $"{icon} {T(lang, $"readiness.signal.{s.Name}")}: {value}{baseline}";
    }
}
