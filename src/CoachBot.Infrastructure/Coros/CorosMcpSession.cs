using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CoachBot.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CoachBot.Infrastructure.Coros;

public sealed class CorosToolException(string tool, string message) : Exception($"COROS tool {tool} failed: {message}")
{
    public string Tool { get; } = tool;
}

/// <summary>
/// One authenticated MCP session per DI scope. Refreshes the access token shortly before expiry, retries read tools
/// on transient failures, and never retries write tools.
/// </summary>
public sealed class CorosMcpSession(
    ICoachDb db,
    CorosOAuthClient oauth,
    TokenCipher cipher,
    IHttpClientFactory httpFactory,
    IOptions<CorosOptions> options,
    ILoggerFactory loggerFactory,
    TimeProvider time) : IAsyncDisposable
{
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private readonly ILogger _logger = loggerFactory.CreateLogger<CorosMcpSession>();
    private McpClient? _client;
    private Guid _athleteId;

    public async Task<IList<McpClientTool>> ListToolsAsync(Guid athleteId, CancellationToken ct)
    {
        var client = await GetClientAsync(athleteId, ct);
        return await client.ListToolsAsync(cancellationToken: ct);
    }

    /// <summary>Calls a tool and returns its payload as JSON (structured content, or the text block parsed as JSON).</summary>
    public async Task<JsonElement> CallAsync(Guid athleteId, string tool, IReadOnlyDictionary<string, object?> args, bool isWrite, CancellationToken ct)
    {
        var attempts = isWrite ? 1 : 3;
        for (var attempt = 1; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var client = await GetClientAsync(athleteId, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ToolTimeoutSeconds));
                var result = await client.CallToolAsync(tool, args, cancellationToken: timeout.Token);
                _logger.LogInformation("COROS tool {Tool} completed in {Ms} ms (error={IsError})", tool, sw.ElapsedMilliseconds, result.IsError == true);
                if (result.IsError == true)
                {
                    throw new CorosToolException(tool, Truncate(TextOf(result)));
                }

                return Payload(result);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized && attempt < 2)
            {
                _logger.LogWarning("COROS returned 401 for {Tool}; forcing token refresh", tool);
                await ResetAsync();
                await ForceRefreshAsync(athleteId, ct);
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "COROS tool {Tool} attempt {Attempt} failed; retrying", tool, attempt);
                await ResetAsync();
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), time, ct);
            }
        }
    }

    public static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static JsonElement Payload(CallToolResult result)
    {
        if (result.StructuredContent is JsonElement structured && structured.ValueKind != JsonValueKind.Undefined)
        {
            return structured.Clone();
        }

        var text = TextOf(result);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { text });
        }
    }

    private async Task<McpClient> GetClientAsync(Guid athleteId, CancellationToken ct)
    {
        if (_client is not null && _athleteId == athleteId)
        {
            return _client;
        }

        await ResetAsync();
        var (token, url) = await GetAccessTokenAsync(athleteId, ct);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "coros",
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, httpFactory.CreateClient("coros-mcp"), loggerFactory, ownsHttpClient: false);

        _client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "coachbot", Version = "0.1.0" },
        }, loggerFactory, ct);
        _athleteId = athleteId;
        return _client;
    }

    private async Task<(string Token, string Url)> GetAccessTokenAsync(Guid athleteId, CancellationToken ct)
    {
        var connection = await db.CorosConnections.FirstOrDefaultAsync(c => c.AthleteId == athleteId, ct)
                         ?? throw new CorosAuthException("COROS is not connected. Use /connect.");
        if (connection.EncryptedAccessToken is not null && connection.AccessTokenExpiresAt > time.GetUtcNow().AddMinutes(2))
        {
            return (cipher.Decrypt(connection.EncryptedAccessToken), connection.McpUrl);
        }

        await ForceRefreshAsync(athleteId, ct);
        await db.CorosConnections.Entry(connection).ReloadAsync(ct);
        return (cipher.Decrypt(connection.EncryptedAccessToken!), connection.McpUrl);
    }

    private async Task ForceRefreshAsync(Guid athleteId, CancellationToken ct)
    {
        await RefreshLock.WaitAsync(ct);
        try
        {
            var connection = await db.CorosConnections.FirstAsync(c => c.AthleteId == athleteId, ct);
            await db.CorosConnections.Entry(connection).ReloadAsync(ct);
            if (connection.EncryptedRefreshToken is null)
            {
                throw new CorosAuthException("COROS session expired and no refresh token is stored. Use /connect.");
            }

            var (resource, server) = await oauth.DiscoverAsync(new Uri(connection.McpUrl), ct);
            TokenResponse token;
            try
            {
                var client = new OAuthClient(connection.ClientId, connection.EncryptedClientSecret is null ? null : cipher.Decrypt(connection.EncryptedClientSecret));
                token = await oauth.RefreshAsync(server.TokenEndpoint, client, cipher.Decrypt(connection.EncryptedRefreshToken), resource.Resource, ct);
            }
            catch (CorosOAuthException ex) when (ex.Error is "invalid_grant" or "invalid_client" or "unauthorized_client")
            {
                connection.EncryptedAccessToken = null;
                connection.EncryptedRefreshToken = null;
                await db.SaveChangesAsync(ct);
                throw new CorosAuthException($"COROS refresh rejected ({ex.Error}). Use /connect.");
            }

            var now = time.GetUtcNow();
            connection.EncryptedAccessToken = cipher.Encrypt(token.AccessToken);
            if (token.RefreshToken is not null)
            {
                connection.EncryptedRefreshToken = cipher.Encrypt(token.RefreshToken);
            }

            connection.AccessTokenExpiresAt = token.ExpiresIn is int s ? now.AddSeconds(s) : now.AddMinutes(30);
            connection.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("COROS access token refreshed");
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or TimeoutException or OperationCanceledException or McpException
        && ex is not CorosAuthException;

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;

    private async Task ResetAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}
