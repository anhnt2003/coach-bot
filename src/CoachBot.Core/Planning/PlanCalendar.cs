namespace CoachBot.Core.Planning;

/// <summary>Calendar arithmetic for plans: weeks run Monday to Sunday and distances are rounded down to 0.5 km.</summary>
public static class PlanCalendar
{
    public static double Floor05(double km) => Math.Floor(km * 2) / 2;

    /// <summary>0 for Monday … 6 for Sunday.</summary>
    public static int MondayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-MondayIndex(date.DayOfWeek));

    /// <summary>A plan starts on the first Monday strictly after today.</summary>
    public static DateOnly MondayAfter(DateOnly today) => MondayOf(today).AddDays(7);

    /// <summary>Weeks from the plan start to the race week, both included.</summary>
    public static int WeeksUntil(DateOnly start, DateOnly raceDate) => (MondayOf(raceDate).DayNumber - start.DayNumber) / 7 + 1;
}
