using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CoachBot.Infrastructure.Coros;

public sealed record ProtectedResourceMetadata(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("authorization_servers")] string[] AuthorizationServers,
    [property: JsonPropertyName("scopes_supported")] string[]? ScopesSupported);

public sealed record AuthServerMetadata(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("device_authorization_endpoint")] string? DeviceAuthorizationEndpoint,
    [property: JsonPropertyName("registration_endpoint")] string? RegistrationEndpoint,
    [property: JsonPropertyName("revocation_endpoint")] string? RevocationEndpoint,
    [property: JsonPropertyName("grant_types_supported")] string[]? GrantTypesSupported);

public sealed record DeviceAuthorization(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int? Interval);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope);

/// <summary>OAuth client identity. With a secret, requests authenticate via HTTP Basic (client_secret_basic).</summary>
public sealed record OAuthClient(string Id, string? Secret);

public sealed class CorosOAuthException(string error, string? description, HttpStatusCode status)
    : Exception($"OAuth error '{error}' ({(int)status}): {description}")
{
    public string Error { get; } = error;
}

/// <summary>
/// Standards-based OAuth client for the COROS MCP authorization server: RFC 9728 discovery, RFC 8414 metadata,
/// RFC 7591 dynamic client registration, RFC 8628 device flow, RFC 7636 PKCE and RFC 8707 resource indicators.
/// Token requests are never retried automatically (refresh tokens may rotate).
/// </summary>
public sealed class CorosOAuthClient(HttpClient http, ILogger<CorosOAuthClient> logger)
{
    public const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly string[] CodeResponseType = ["code"];

    public async Task<(ProtectedResourceMetadata Resource, AuthServerMetadata Server)> DiscoverAsync(Uri mcpUrl, CancellationToken ct)
    {
        var origin = mcpUrl.GetLeftPart(UriPartial.Authority);
        var path = mcpUrl.AbsolutePath.TrimEnd('/');
        var prm = await GetJsonAsync<ProtectedResourceMetadata>($"{origin}/.well-known/oauth-protected-resource{path}", ct)
                  ?? await GetJsonAsync<ProtectedResourceMetadata>($"{origin}/.well-known/oauth-protected-resource", ct)
                  ?? throw new InvalidOperationException($"No OAuth protected-resource metadata at {origin}.");

        var issuer = prm.AuthorizationServers.FirstOrDefault()?.TrimEnd('/')
                     ?? throw new InvalidOperationException("Protected-resource metadata lists no authorization server.");
        var server = await GetJsonAsync<AuthServerMetadata>($"{issuer}/.well-known/oauth-authorization-server", ct)
                     ?? await GetJsonAsync<AuthServerMetadata>($"{issuer}/.well-known/openid-configuration", ct)
                     ?? throw new InvalidOperationException($"No authorization server metadata at {issuer}.");

        logger.LogInformation("COROS OAuth discovered: resource={Resource} issuer={Issuer} device_flow={Device} dcr={Dcr}",
            prm.Resource, server.Issuer, server.DeviceAuthorizationEndpoint is not null, server.RegistrationEndpoint is not null);
        return (prm, server);
    }

    public async Task<OAuthClient> RegisterClientAsync(AuthServerMetadata server, string clientName, IReadOnlyList<string> redirectUris,
        IReadOnlyList<string> grantTypes, string scope, string authMethod, CancellationToken ct)
    {
        if (server.RegistrationEndpoint is null)
        {
            throw new InvalidOperationException("Authorization server does not support dynamic client registration.");
        }

        using var response = await http.PostAsJsonAsync(server.RegistrationEndpoint, new Dictionary<string, object>
        {
            ["client_name"] = clientName,
            ["redirect_uris"] = redirectUris,
            ["grant_types"] = grantTypes,
            ["response_types"] = CodeResponseType,
            ["token_endpoint_auth_method"] = authMethod,
            ["scope"] = scope,
        }, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw ToOAuthException(body, response.StatusCode);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var clientId = root.GetProperty("client_id").GetString()
                       ?? throw new InvalidOperationException("Registration response has no client_id.");
        var secret = root.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
        logger.LogInformation("Registered COROS OAuth client (auth={AuthMethod}, secret issued={HasSecret}, grants={GrantTypes})",
            root.TryGetProperty("token_endpoint_auth_method", out var m) ? m.GetString() : authMethod, secret is not null,
            root.TryGetProperty("grant_types", out var g) ? g.ToString() : string.Join(",", grantTypes));
        return new OAuthClient(clientId, secret);
    }

    public async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(AuthServerMetadata server, OAuthClient client, string scope, string resource, CancellationToken ct)
    {
        if (server.DeviceAuthorizationEndpoint is null)
        {
            throw new InvalidOperationException("Authorization server does not support the device authorization grant.");
        }

        var response = await PostFormAsync<DeviceAuthorization>(server.DeviceAuthorizationEndpoint, client, new()
        {
            ["scope"] = scope,
            ["resource"] = resource,
        }, ct);
        return response;
    }

    /// <summary>Polls the token endpoint per RFC 8628 §3.5 until the user approves, denies or the code expires.</summary>
    public async Task<TokenResponse> PollDeviceTokenAsync(AuthServerMetadata server, OAuthClient client, DeviceAuthorization device, string resource, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval ?? 5, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            try
            {
                return await PostFormAsync<TokenResponse>(server.TokenEndpoint, client, new()
                {
                    ["grant_type"] = DeviceCodeGrant,
                    ["device_code"] = device.DeviceCode,
                    ["resource"] = resource,
                }, ct);
            }
            catch (CorosOAuthException ex) when (ex.Error == "authorization_pending")
            {
            }
            catch (CorosOAuthException ex) when (ex.Error == "slow_down")
            {
                interval += TimeSpan.FromSeconds(5);
            }
        }

        throw new CorosOAuthException("expired_token", "The device code expired before it was approved.", HttpStatusCode.BadRequest);
    }

    public Task<TokenResponse> RefreshAsync(string tokenEndpoint, OAuthClient client, string refreshToken, string resource, CancellationToken ct) =>
        PostFormAsync<TokenResponse>(tokenEndpoint, client, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["resource"] = resource,
        }, ct);

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string BuildAuthorizeUrl(AuthServerMetadata server, string clientId, string redirectUri, string scope, string state, string challenge, string resource)
    {
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = resource,
        }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return $"{server.AuthorizationEndpoint}?{query}";
    }

    public Task<TokenResponse> ExchangeCodeAsync(AuthServerMetadata server, OAuthClient client, string code, string redirectUri, string verifier, string resource, CancellationToken ct) =>
        PostFormAsync<TokenResponse>(server.TokenEndpoint, client, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["resource"] = resource,
        }, ct);

    /// <summary>Confidential clients authenticate with HTTP Basic (RFC 6749 §2.3.1); public clients send client_id.</summary>
    private async Task<T> PostFormAsync<T>(string url, OAuthClient client, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (client.Secret is null)
        {
            form["client_id"] = client.Id;
        }
        else
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(client.Id)}:{Uri.EscapeDataString(client.Secret)}"));
            request.Headers.Authorization = new("Basic", basic);
        }

        request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw ToOAuthException(body, response.StatusCode);
        }

        return JsonSerializer.Deserialize<T>(body) ?? throw new InvalidOperationException($"Empty response from {url}.");
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    private static CorosOAuthException ToOAuthException(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown" : "unknown";
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return new CorosOAuthException(error, description, status);
        }
        catch (JsonException)
        {
            return new CorosOAuthException("http_error", body.Length > 300 ? body[..300] : body, status);
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
