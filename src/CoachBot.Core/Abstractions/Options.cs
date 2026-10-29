using System.ComponentModel.DataAnnotations;

namespace CoachBot.Core.Abstractions;

public sealed class CoachOptions
{
    public const string Section = "Coach";

    [Required]
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";

    [RegularExpression("^(en|vi)$")]
    public string Language { get; set; } = "en";

    /// <summary>Public URL of this service; only needed for the COROS PKCE redirect fallback.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>How many days ahead approved workouts are pushed to COROS.</summary>
    [Range(1, 14)]
    public int PushWindowDays { get; set; } = 7;

    /// <summary>How long a proposed plan change waits for the athlete's answer before it expires.</summary>
    [Range(1, 168)]
    public int ApprovalExpiryHours { get; set; } = 48;
}
