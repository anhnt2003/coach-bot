using System.Globalization;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Planning;

namespace CoachBot.Core.Services;

/// <summary>
/// Renders workouts and numbers into localized text. All user-facing strings come from <see cref="ITextCatalog"/>.
/// <see cref="T"/> and the methods returning <see cref="Html"/> build Telegram messages; the plain ones
/// (<see cref="Text"/>, <see cref="Description"/>, <see cref="Steps"/>…) build workout texts that are stored and pushed
/// to COROS, so they never contain markup.
/// </summary>
public sealed partial class MessageFormatter(ITextCatalog text)
{
    /// <summary>A message template filled in: plain string arguments are escaped, <see cref="Html"/> ones kept as markup.</summary>
    public Html T(string lang, string key, params object?[] args) =>
        new(text.Get(lang, key, [.. args.Select(a => a switch
        {
            Html html => html.Value,
            string plain => Html.Encode(plain),
            _ => a,
        })]));

    /// <summary>A plain-text template (workout texts): no markup, nothing escaped.</summary>
    public string Text(string lang, string key, params object?[] args) => text.Get(lang, key, args);

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
                return Text(lang, $"race.distance.{key}");
            }
        }

        return $"{Km(km)} km";
    }

    public static CultureInfo Culture(string lang) => CultureInfo.GetCultureInfo(lang == "vi" ? "vi-VN" : "en-GB");

    private static readonly string[] ViDays = ["CN", "T2", "T3", "T4", "T5", "T6", "T7"];

    /// <summary>"T3 17/11" / "Tue 17/11".</summary>
    public string Day(string lang, DateOnly date) =>
        $"{(lang == "vi" ? ViDays[(int)date.DayOfWeek] : date.ToString("ddd", Culture(lang)))} {date:dd/MM}";

    /// <summary>"16/11 – 22/11".</summary>
    public static string Dates(DateOnly from, DateOnly to) => $"{from:dd/MM} – {to:dd/MM}";

    public string DayName(string lang, DayOfWeek day) => Culture(lang).DateTimeFormat.GetDayName(day);

    public string TypeName(string lang, WorkoutType type) => Text(lang, $"workout.type.{type.ToString().ToLowerInvariant()}");

    public string Title(string lang, WorkoutType type, double km) =>
        type == WorkoutType.Rest ? TypeName(lang, type) : $"{TypeName(lang, type)} {Km(km)} km";

    /// <summary>Session structure plus targets (pace/HR, or RPE when no physiological data exists).</summary>
    public string Description(string lang, PlannedWorkout w)
    {
        if (WorkoutStructure.Parse(w.StepsJson) is { Count: > 0 } blocks)
        {
            // Paces are part of the steps; add heart rate, or effort when there is no pace or HR data at all.
            var extra = w.HrMin is not null ? Text(lang, "workout.target.hr", w.HrMin, w.HrMax)
                : w.PaceMinSecPerKm is null && blocks.All(b => b.Work.PaceFast is null) ? Text(lang, $"workout.rpe.{w.Type.ToString().ToLowerInvariant()}")
                : null;
            return extra is null ? Steps(lang, blocks) : $"{Steps(lang, blocks)} · {extra}";
        }

        var km = w.TargetDistanceKm;
        var structure = w.Type switch
        {
            WorkoutType.Tempo => Text(lang, "workout.desc.tempo", Km(Math.Max(km - 4, km * 0.5))),
            WorkoutType.Interval => Text(lang, "workout.desc.interval", Math.Max(3, (int)Math.Round(km * 0.4))),
            WorkoutType.RacePace => Text(lang, "workout.desc.race_pace", Km(PlanCalendar.Floor05(km * 0.6))),
            WorkoutType.Long => Text(lang, "workout.desc.long"),
            WorkoutType.Recovery => Text(lang, "workout.desc.recovery"),
            WorkoutType.Easy => Text(lang, "workout.desc.easy"),
            WorkoutType.Race => Text(lang, "workout.desc.race"),
            _ => Text(lang, "workout.desc.rest"),
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
        var label = Text(lang, $"intensity.{PlanDesignMapper.IntensityName(p.Intensity)}");
        var showPace = p.PaceFast is not null && p.Intensity is not (Intensity.Easy or Intensity.Recovery);
        return showPace ? $"{amount} {label} {Pace(p.PaceFast)}–{Pace(p.PaceSlow)}/km" : $"{amount} {label}";
    }

    public string Targets(string lang, PlannedWorkout w) => Targets(lang, w.Type, w.PaceMinSecPerKm, w.PaceMaxSecPerKm, w.HrMin, w.HrMax);

    private string Targets(string lang, WorkoutType type, int? paceMin, int? paceMax, int? hrMin, int? hrMax)
    {
        var parts = new List<string>();
        if (paceMin is not null)
        {
            parts.Add(Text(lang, "workout.target.pace", Pace(paceMin), Pace(paceMax)));
        }

        if (hrMin is not null)
        {
            parts.Add(Text(lang, "workout.target.hr", hrMin, hrMax));
        }

        if (parts.Count == 0)
        {
            parts.Add(Text(lang, $"workout.rpe.{type.ToString().ToLowerInvariant()}"));
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
            w.Description = string.IsNullOrWhiteSpace(notes) ? Text(lang, "workout.desc.rest") : notes.Trim();
            return;
        }

        var templated = Description(lang, w);
        w.Title = string.IsNullOrWhiteSpace(title) ? Title(lang, w.Type, w.TargetDistanceKm) : $"{title.Trim()} · {Km(w.TargetDistanceKm)} km";
        w.Description = string.IsNullOrWhiteSpace(notes) ? templated
            : w.StepsJson is null ? $"{notes.Trim()} {Targets(lang, w)}"
            : $"{notes.Trim()}\n{templated}";
    }

    /// <summary>"Easy run 6 km · 6:30–7:00/km · HR 140–150", or "Rest".</summary>
    public string Summary(string lang, WorkoutState s)
    {
        var title = WorkoutTitle(lang, s);
        return s.Type == WorkoutType.Rest ? title : $"{title} · {Targets(lang, s.Type, s.PaceMin, s.PaceMax, s.HrMin, s.HrMax)}";
    }

    private string WorkoutTitle(string lang, WorkoutState s) => string.IsNullOrEmpty(s.Title) ? Title(lang, s.Type, s.Km) : s.Title;

    /// <summary>
    /// A session in a list: "<b>Tue 17/11</b> · Easy run 7 km ✅" and, unless it is a rest day, its targets on a second line.
    /// </summary>
    public Html Line(string lang, PlannedWorkout w)
    {
        var mark = w.Status switch
        {
            WorkoutStatus.Completed => " ✅",
            WorkoutStatus.Missed => " ❌",
            WorkoutStatus.Skipped => " ⏭",
            _ => "",
        };

        return Line(lang, PlanAdjuster.State(w), mark);
    }

    public Html Line(string lang, WorkoutState s, string mark = "")
    {
        var head = new Html($"{Html.Bold(Day(lang, s.Date))} · {Html.Encode(WorkoutTitle(lang, s))}{mark}");
        return s.Type == WorkoutType.Rest
            ? head
            : Html.Lines(head, new Html("   " + Html.Encode(Targets(lang, s.Type, s.PaceMin, s.PaceMax, s.HrMin, s.HrMax))));
    }

    /// <summary>The session of the day: its line, then how to run it.</summary>
    public Html Session(string lang, PlannedWorkout w) =>
        HowTo(lang, w) is { Length: > 0 } howTo ? Html.Lines(Line(lang, w), Html.Italic(Html.Text(howTo))) : Line(lang, w);

    /// <summary>
    /// The stored description without the targets at its end, which <see cref="Line(string, PlannedWorkout)"/> already
    /// shows: "Keep it relaxed. 7:10–8:00/km · HR 140–158" → "Keep it relaxed.". Structured sessions keep their steps.
    /// </summary>
    private string HowTo(string lang, PlannedWorkout w)
    {
        var description = w.Description?.Trim() ?? "";
        if (w.Type == WorkoutType.Rest || w.StepsJson is not null)
        {
            return description;
        }

        var targets = Targets(lang, w);
        return description.EndsWith(targets, StringComparison.Ordinal)
            ? description[..^targets.Length].TrimEnd()
            // Written before the current wording of the targets (e.g. "Nhịp tim" for "HR").
            : TrailingPace().Replace(description, "").TrimEnd();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\d{1,2}:\d{2}–\d{1,2}:\d{2}/km( · [^\n]*)?$")]
    private static partial System.Text.RegularExpressions.Regex TrailingPace();

    /// <summary>
    /// "<b>Tue 27/10</b>", the old session struck through, the new one, and the coach's reason:
    /// "<s>Tempo 8 km · 5:23–5:40/km</s>\n→ Easy run 6 km · 6:30–7:00/km\n<i>HRV is down.</i>".
    /// </summary>
    public Html ChangeItem(string lang, WorkoutChange c)
    {
        var day = c.Before.Date == c.After.Date
            ? Html.Bold(Day(lang, c.Before.Date))
            : new Html($"{Html.Bold(Day(lang, c.Before.Date))} → {Html.Bold(Day(lang, c.After.Date))}");
        return Html.Lines(
            day,
            Html.Strike(Html.Text(Summary(lang, c.Before))),
            new Html("→ " + Html.Encode(Summary(lang, c.After))),
            string.IsNullOrWhiteSpace(c.Reason) ? Html.Empty : Html.Italic(Html.Text(c.Reason.Trim())));
    }

    /// <summary>
    /// What the watch says: running level, VO2max and threshold pace, COROS's prediction for the goal distance, the
    /// training load and recovery. Lines without data are left out.
    /// </summary>
    public Html Watch(string lang, WatchMetrics w, double? goalKm)
    {
        static string N(double? v) => v is double d ? d.ToString("0.##", CultureInfo.InvariantCulture) : "–";
        var fitness = new List<Html>();
        if (w.RunningLevel is not null)
        {
            fitness.Add(T(lang, "watch.running_level", N(w.RunningLevel)));
        }

        if (w.Vo2Max is not null)
        {
            fitness.Add(T(lang, "watch.vo2max", N(w.Vo2Max)));
        }

        if (w.ThresholdPaceSecPerKm is int threshold)
        {
            fitness.Add(T(lang, "watch.threshold", Pace(threshold)));
        }

        var date = w.FitnessDate ?? w.LoadDate;
        return Html.Lines(
            T(lang, "watch.title", date is { } d ? $"{d:dd/MM}" : "–"),
            new Html(string.Join(" · ", fitness.Select(f => f.Value))),
            goalKm is double km && w.PredictionFor(km) is int predicted ? T(lang, "watch.prediction", MidSentence(RaceDistance(lang, km)), Time(predicted)) : Html.Empty,
            w.ShortTermLoad is null && w.LongTermLoad is null && w.LoadRatio is null
                ? Html.Empty
                : new Html(T(lang, "watch.load", N(w.ShortTermLoad), N(w.LongTermLoad), N(w.LoadRatio)).Value
                           + (string.IsNullOrWhiteSpace(w.LoadStatus) ? "" : " · " + Html.Encode(w.LoadStatus))),
            w.RecoveryPct is int recovery ? T(lang, "watch.recovery", recovery) : Html.Empty);
    }

    /// <summary>"Half marathon" → "half marathon" inside a sentence; "5K" stays.</summary>
    private static string MidSentence(string label) =>
        label.Length > 1 && char.IsUpper(label[0]) && char.IsLower(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;

    /// <summary>"🟠 <b>Some fatigue</b> · medium confidence", plus e.g. " · checked Sat 21/11".</summary>
    public Html Readiness(string lang, ReadinessLevel level, Confidence confidence, Html suffix = default) =>
        new(T(lang, $"readiness.level.{level.ToString().ToLowerInvariant()}").Value
            + " · " + T(lang, $"readiness.confidence.{confidence.ToString().ToLowerInvariant()}").Value
            + suffix.Value);

    /// <summary>The readiness verdict: heading, the coach's summary, and the signals behind it collapsed.</summary>
    public Html ReadinessBlock(string lang, ReadinessLevel level, Confidence confidence, string summary, IReadOnlyList<string> concerns, Html suffix = default) =>
        Html.Lines(
            Readiness(lang, level, confidence, suffix),
            Html.Text(summary.Trim()),
            concerns.Count == 0 ? Html.Empty : Html.Expandable(Html.Lines(T(lang, "readiness.signals"), Html.Bullets(concerns))));
}
