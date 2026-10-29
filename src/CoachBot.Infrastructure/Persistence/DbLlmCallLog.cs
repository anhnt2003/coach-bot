using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoachBot.Infrastructure.Persistence;

/// <summary>Writes llm_calls in its own scope, so it never saves the calling service's pending changes.</summary>
public sealed class DbLlmCallLog(IServiceScopeFactory scopes, TimeProvider time, ILogger<DbLlmCallLog> logger) : ILlmCallLog
{
    public async Task RecordAsync(LlmCallRecord record, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ICoachDb>();
            db.LlmCalls.Add(new LlmCall
            {
                AthleteId = record.AthleteId,
                Purpose = record.Purpose,
                Model = record.Model,
                ContextJson = record.ContextJson,
                Response = record.Response,
                Status = record.Failure?.ToString().ToLowerInvariant() ?? "ok",
                Error = record.Error,
                DurationMs = record.DurationMs,
                CreatedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The audit trail must not turn a usable answer into a failure.
            logger.LogError(ex, "Could not record LLM call {Purpose}", record.Purpose);
        }
    }
}
