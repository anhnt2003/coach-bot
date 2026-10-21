using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure.Coros;

/// <summary>
/// Adapter for the official COROS MCP server (https://github.com/coroslab/COROS-MCP).
/// Capabilities are read from tools/list at runtime; features whose tools are missing are disabled, not faked.
/// </summary>
public sealed partial class CorosMcpGateway(
    CorosMcpSession session, ICoachDb db, IOptions<CorosOptions> options, TimeProvider time, ILogger<CorosMcpGateway> logger) : ICorosGateway
{
    public static readonly string[] RequiredReadTools = ["querySportRecords", "queryDailyHealthData"];

    public string Source => "coros";

    public async Task<CorosCapabilities> GetCapabilitiesAsync(Guid athleteId, CancellationToken ct)
    {
        var tools = (await session.ListToolsAsync(athleteId, ct)).Select(t => t.Name).ToHashSet();
        var missing = RequiredReadTools.Where(t => !tools.Contains(t)).ToList();
        if (missing.Count > 0)
        {
            logger.LogError("COROS MCP is missing required read tools: {Missing}", string.Join(", ", missing));
        }

        var connection = await db.CorosConnections.FirstOrDefaultAsync(c => c.AthleteId == athleteId, ct);
        if (connection is not null)
        {
            connection.CapabilitiesJson = CoachJson.Serialize(tools.Order());
            connection.LastVerifiedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        var pushEnabled = options.Value.EnableWorkoutPush;
        if (!pushEnabled && tools.Contains("createScheduledWorkout"))
        {
            logger.LogInformation("COROS offers workout scheduling, but automatic push is off until verified (Coros__EnableWorkoutPush=true)");
        }

        return new CorosCapabilities(tools, missing.Count == 0,
            pushEnabled && tools.Contains("createScheduledWorkout"),
            pushEnabled && tools.Contains("updateScheduledWorkout"));
    }
}
