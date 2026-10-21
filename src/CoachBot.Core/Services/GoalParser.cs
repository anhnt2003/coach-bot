using System.Globalization;
using System.Text.RegularExpressions;
using CoachBot.Core.Domain;

namespace CoachBot.Core.Services;

public sealed record GoalInput(DateOnly Date, double DistanceKm, int? GoalSeconds, string Name);

/// <summary>
/// Parses "/goal" arguments: a date, then (in any order) an optional distance and an optional goal time, then the race
/// name. Examples: "2027-03-07 3:59 Hanoi Marathon", "2026-12-20 half 1:45", "2026-11-30 5k 22:30 Parkrun".
/// Without a distance it is read from the name ("… Half Marathon", "… 10K"), defaulting to the marathon.
/// </summary>
public static partial class GoalParser
{
    private const int MinPaceSecPerKm = 150;
    private const int MaxPaceSecPerKm = 720;

    public static bool TryParse(string args, out GoalInput? goal, out string? errorKey)
    {
        goal = null;
        errorKey = "goal.usage";
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !DateOnly.TryParseExact(tokens[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return false;
        }

        double? distance = null;
        string? time = null;
        var i = 1;
        for (; i < tokens.Length && i <= 2; i++)
        {
            if (distance is null && TryDistance(tokens[i], out var km))
            {
                if (km is < 3 or > 42.2)
                {
                    errorKey = "goal.invalid_distance";
                    return false;
                }

                distance = km;
            }
            else if (time is null && TimeRegex().IsMatch(tokens[i]))
            {
                time = tokens[i];
            }
            else
            {
                break;
            }
        }

        var name = string.Join(' ', tokens[i..]);
        distance ??= FromName(name);
        int? seconds = null;
        if (time is not null)
        {
            seconds = GoalSeconds(time, distance.Value);
            if (seconds is null)
            {
                errorKey = "goal.invalid_time";
                return false;
            }
        }

        if (name.Length == 0)
        {
            name = DefaultName(distance.Value);
        }

        goal = new GoalInput(date, distance.Value, seconds, name);
        errorKey = null;
        return true;
    }

    /// <summary>
    /// "H:MM:SS", or "A:B" read as hours:minutes or minutes:seconds, whichever gives a plausible pace for the distance
    /// (3:59 is a marathon time, 22:30 a 5K time; both readings can never be plausible at once).
    /// </summary>
    internal static int? GoalSeconds(string time, double distanceKm)
    {
        var parts = time.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (parts.Skip(1).Any(p => p >= 60))
        {
            return null;
        }

        int[] candidates = parts.Length == 3
            ? [parts[0] * 3600 + parts[1] * 60 + parts[2]]
            : [parts[0] * 3600 + parts[1] * 60, parts[0] * 60 + parts[1]];
        return candidates.Where(s => s / distanceKm is >= MinPaceSecPerKm and <= MaxPaceSecPerKm).Cast<int?>().FirstOrDefault();
    }

    private static bool TryDistance(string token, out double km)
    {
        km = token.ToLowerInvariant() switch
        {
            "half" or "hm" or "halfmarathon" or "half-marathon" => RaceProfiles.HalfKm,
            "full" or "fm" or "marathon" => RaceProfiles.MarathonKm,
            _ => 0,
        };
        if (km > 0)
        {
            return true;
        }

        var match = DistanceRegex().Match(token);
        if (!match.Success)
        {
            return false;
        }

        km = double.Parse(match.Groups["km"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        // "21k"/"21.1k" and "42k"/"42.2k" mean the standard distances.
        km = km switch
        {
            >= 21 and < 21.2 => RaceProfiles.HalfKm,
            >= 42 and < 42.3 => RaceProfiles.MarathonKm,
            _ => km,
        };
        return true;
    }

    private static double FromName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("half", StringComparison.Ordinal) || n.Contains("bán marathon", StringComparison.Ordinal)
            || n.Contains("ban marathon", StringComparison.Ordinal) || HalfInName().IsMatch(n))
        {
            return RaceProfiles.HalfKm;
        }

        return TenKInName().IsMatch(n) ? RaceProfiles.TenKKm : FiveKInName().IsMatch(n) ? RaceProfiles.FiveKKm : RaceProfiles.MarathonKm;
    }

    private static string DefaultName(double km) => RaceProfiles.For(km) switch
    {
        RaceProfile.Marathon when km > 42 => "Marathon",
        RaceProfile.Half when Math.Abs(km - RaceProfiles.HalfKm) < 0.1 => "Half Marathon",
        RaceProfile.TenK when Math.Abs(km - 10) < 0.1 => "10K",
        RaceProfile.FiveK when Math.Abs(km - 5) < 0.1 => "5K",
        _ => $"{km.ToString("0.#", CultureInfo.InvariantCulture)} km race",
    };

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^(?<km>\d{1,2}([.,]\d{1,4})?)\s?(k|km)$", RegexOptions.IgnoreCase)]
    private static partial Regex DistanceRegex();

    [GeneratedRegex(@"\b21([.,]1)?\s?km?\b")]
    private static partial Regex HalfInName();

    [GeneratedRegex(@"\b10\s?km?\b")]
    private static partial Regex TenKInName();

    [GeneratedRegex(@"\b5\s?km?\b")]
    private static partial Regex FiveKInName();
}
