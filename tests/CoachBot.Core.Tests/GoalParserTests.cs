using CoachBot.Core.Services;

namespace CoachBot.Core.Tests;

public class GoalDistanceTests
{
    [Theory]
    [InlineData("2027-03-07 3:59 Hanoi Marathon", 42.195, 3 * 3600 + 59 * 60, "Hanoi Marathon")]
    [InlineData("2026-12-20 half 1:45", 21.0975, 6300, "Half Marathon")]
    [InlineData("2026-12-20 half marathon 1:45", 21.0975, 6300, "Half Marathon")]
    [InlineData("2027-03-07 Full Marathon 3:59 Hanoi", 42.195, 3 * 3600 + 59 * 60, "Hanoi")]
    [InlineData("2026-12-20 1:45 half VnExpress", 21.0975, 6300, "VnExpress")]
    [InlineData("2026-12-20 1:45 VnExpress Half Marathon", 21.0975, 6300, "VnExpress Half Marathon")]
    [InlineData("2026-12-20 Giải bán marathon Huế", 21.0975, null, "Giải bán marathon Huế")]
    [InlineData("2026-11-29 5k 22:30", 5, 1350, "5K")]
    [InlineData("2026-11-29 10K 0:48 Run", 10, 2880, "Run")]
    [InlineData("2026-11-29 10k 48:00", 10, 2880, "10K")]
    [InlineData("2026-11-29 21.1k", 21.0975, null, "Half Marathon")]
    [InlineData("2026-11-29 15km 1:10:00 City run", 15, 4200, "City run")]
    [InlineData("2026-11-29", 42.195, null, "Marathon")]
    public void Parses_distance_time_and_name(string args, double km, int? seconds, string name)
    {
        Assert.True(GoalParser.TryParse(args, out var goal, out _));
        Assert.Equal(km, goal!.DistanceKm, 3);
        Assert.Equal(seconds, goal.GoalSeconds);
        Assert.Equal(name, goal.Name);
    }

    [Theory]
    [InlineData("next sunday", "goal.usage")]
    [InlineData("2026-11-29 5k 1:30", "goal.invalid_time")]
    [InlineData("2026-11-29 marathon 1:30", "goal.invalid_time")]
    [InlineData("2026-11-29 50k", "goal.invalid_distance")]
    public void Rejects_unusable_goals(string args, string error)
    {
        Assert.False(GoalParser.TryParse(args, out _, out var key));
        Assert.Equal(error, key);
    }
}
