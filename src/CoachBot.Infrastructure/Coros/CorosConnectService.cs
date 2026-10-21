using System.Collections.Concurrent;
using System.Security.Cryptography;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure.Coros;

public sealed record DeviceLoginStarted(string UserCode, string VerificationUri, string? VerificationUriComplete, int ExpiresInSeconds, DeviceLoginContext Context);

public sealed record DeviceLoginContext(AuthServerMetadata Server, OAuthClient Client, string Resource, DeviceAuthorization Device);

/// <summary>Pending browser (PKCE) logins, keyed by OAuth state. Single-instance app, so in-memory is enough.</summary>
public sealed class PendingBrowserLogins
{
    public ConcurrentDictionary<string, (Guid AthleteId, string Verifier, string RedirectUri, AuthServerMetadata Server, OAuthClient Client, string Resource, DateTimeOffset Created)> Items { get; } = new();
}

/// <summary>Connects an athlete's COROS account: discovery → client registration → user authorization → encrypted token storage.</summary>
public sealed class CorosConnectService(
    ICoachDb db,
    CorosOAuthClient oauth,
    TokenCipher cipher,
    PendingBrowserLogins pending,
    IOptions<CorosOptions> options,
    IOptions<CoachOptions> coach,
    TimeProvider time,
    ILogger<CorosConnectService> logger)
{
    private string Scope => options.Value.Scopes;

    public string RedirectUri => $"{(coach.Value.PublicBaseUrl ?? "http://localhost:8080").TrimEnd('/')}/oauth/coros/callback";

    public async Task<DeviceLoginStarted> StartDeviceLoginAsync(Guid athleteId, CancellationToken ct)
    {
        var (resource, server) = await oauth.DiscoverAsync(new Uri(options.Value.McpUrl), ct);
        var client = await EnsureClientAsync(athleteId, resource, server, ct);
        var device = await oauth.StartDeviceAuthorizationAsync(server, client, Scope, resource.Resource, ct);
        return new DeviceLoginStarted(device.UserCode, device.VerificationUri, device.VerificationUriComplete, device.ExpiresIn,
            new DeviceLoginContext(server, client, resource.Resource, device));
    }

    public async Task CompleteDeviceLoginAsync(Guid athleteId, DeviceLoginContext ctx, CancellationToken ct)
    {
        var token = await oauth.PollDeviceTokenAsync(ctx.Server, ctx.Client, ctx.Device, ctx.Resource, ct);
        await StoreTokensAsync(athleteId, token, ct);
    }

    /// <summary>Fallback when the device grant is not allowed: returns a URL to open; the callback endpoint completes it.</summary>
    public async Task<string> StartBrowserLoginAsync(Guid athleteId, CancellationToken ct)
    {
        var (resource, server) = await oauth.DiscoverAsync(new Uri(options.Value.McpUrl), ct);
        var client = await EnsureClientAsync(athleteId, resource, server, ct);
        var (verifier, challenge) = CorosOAuthClient.CreatePkce();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        pending.Items[state] = (athleteId, verifier, RedirectUri, server, client, resource.Resource, time.GetUtcNow());
        return CorosOAuthClient.BuildAuthorizeUrl(server, client.Id, RedirectUri, Scope, state, challenge, resource.Resource);
    }

    /// <summary>
    /// Completes a browser login from the full redirect URL the athlete copied from the address bar. Lets a server
    /// without a public URL finish the login (the redirect to localhost fails to load, but carries code and state).
    /// </summary>
    public Task<bool> CompleteFromRedirectUrlAsync(string redirectUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(redirectUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return Task.FromResult(false);
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue("code", out var code) && query.TryGetValue("state", out var state)
            ? CompleteBrowserLoginAsync(state.ToString(), code.ToString(), ct)
            : Task.FromResult(false);
    }

    public async Task<bool> CompleteBrowserLoginAsync(string state, string code, CancellationToken ct)
    {
        if (!pending.Items.TryRemove(state, out var p) || time.GetUtcNow() - p.Created > TimeSpan.FromMinutes(15))
        {
            return false;
        }

        var token = await oauth.ExchangeCodeAsync(p.Server, p.Client, code, p.RedirectUri, p.Verifier, p.Resource, ct);
        await StoreTokensAsync(p.AthleteId, token, ct);
        return true;
    }

    public async Task DisconnectAsync(Guid athleteId, CancellationToken ct)
    {
        var connection = await db.CorosConnections.FirstOrDefaultAsync(c => c.AthleteId == athleteId, ct);
        if (connection is not null)
        {
            connection.EncryptedAccessToken = null;
            connection.EncryptedRefreshToken = null;
            connection.AccessTokenExpiresAt = null;
            connection.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Reuses the registered client for this issuer, or registers one (RFC 7591).
    /// Verified live (2026-10-21): COROS registers every dynamic client as public (auth "none", no secret) with only the
    /// authorization_code + refresh_token grants, even when the device grant or a secret is requested. A secret is still
    /// stored and used if the server ever issues one.
    /// </summary>
    private async Task<OAuthClient> EnsureClientAsync(Guid athleteId, ProtectedResourceMetadata resource, AuthServerMetadata server, CancellationToken ct)
    {
        var connection = await db.CorosConnections.FirstOrDefaultAsync(c => c.AthleteId == athleteId, ct);
        if (connection is not null && connection.Issuer == server.Issuer && !string.IsNullOrEmpty(connection.ClientId))
        {
            connection.McpUrl = resource.Resource;
            await db.SaveChangesAsync(ct);
            return new OAuthClient(connection.ClientId, connection.EncryptedClientSecret is null ? null : cipher.Decrypt(connection.EncryptedClientSecret));
        }

        var grants = new List<string> { "authorization_code", "refresh_token" };
        if (server.DeviceAuthorizationEndpoint is not null)
        {
            grants.Insert(0, CorosOAuthClient.DeviceCodeGrant);
        }

        var client = await oauth.RegisterClientAsync(server, options.Value.ClientName, [RedirectUri], grants, Scope, "none", ct);
        var now = time.GetUtcNow();
        if (connection is null)
        {
            connection = new CorosConnection { AthleteId = athleteId, CreatedAt = now };
            db.CorosConnections.Add(connection);
        }

        // The protected-resource metadata names the canonical (regional) MCP endpoint; tokens are bound to it.
        connection.McpUrl = resource.Resource;
        connection.Issuer = server.Issuer;
        connection.ClientId = client.Id;
        connection.EncryptedClientSecret = client.Secret is null ? null : cipher.Encrypt(client.Secret);
        connection.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return client;
    }

    private async Task StoreTokensAsync(Guid athleteId, TokenResponse token, CancellationToken ct)
    {
        var connection = await db.CorosConnections.FirstAsync(c => c.AthleteId == athleteId, ct);
        var now = time.GetUtcNow();
        connection.EncryptedAccessToken = cipher.Encrypt(token.AccessToken);
        connection.EncryptedRefreshToken = token.RefreshToken is null ? connection.EncryptedRefreshToken : cipher.Encrypt(token.RefreshToken);
        connection.AccessTokenExpiresAt = token.ExpiresIn is int s ? now.AddSeconds(s) : now.AddMinutes(30);
        connection.Scopes = token.Scope;
        connection.ConsecutiveSyncFailures = 0;
        connection.LastSyncError = null;
        connection.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("COROS tokens stored (refresh token: {HasRefresh}, expires in {Expires}s)", token.RefreshToken is not null, token.ExpiresIn);
    }
}
