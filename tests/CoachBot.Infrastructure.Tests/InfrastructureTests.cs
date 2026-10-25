using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoachBot.Core.Abstractions;
using CoachBot.Infrastructure.Coros;
using CoachBot.Infrastructure.Llm;
using CoachBot.Infrastructure.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CoachBot.Infrastructure.Tests;

/// <summary>Records requests and replies from a queue of canned responses.</summary>
internal sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _index;
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return responses[Math.Min(_index++, responses.Length - 1)](request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class LocalizationTests
{
    private static readonly JsonTextCatalog Catalog = new(strict: true);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CoachBot.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }

    [Fact]
    public void English_and_vietnamese_have_identical_keys()
    {
        Assert.Contains("en", Catalog.Languages);
        Assert.Contains("vi", Catalog.Languages);
        Assert.True(Catalog.Keys("en").Count > 100, "catalog not loaded from embedded resources");
        var en = Catalog.Keys("en").ToHashSet();
        var vi = Catalog.Keys("vi").ToHashSet();
        Assert.Empty(en.Except(vi));
        Assert.Empty(vi.Except(en));
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        foreach (var key in Catalog.Keys("en"))
        {
            var en = Regex.Matches(Catalog.Get("en", key), @"\{(\d+)\}").Select(m => m.Value).ToHashSet();
            var vi = Regex.Matches(Catalog.Get("vi", key), @"\{(\d+)\}").Select(m => m.Value).ToHashSet();
            Assert.True(en.SetEquals(vi), $"{key}: en {string.Join(",", en)} vs vi {string.Join(",", vi)}");
        }
    }

    [Fact]
    public void Every_key_referenced_in_source_exists()
    {
        var root = RepoRoot();
        var source = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        var prefixes = new[] { "adapt.", "alert.", "analysis.", "assess.", "button.", "change.", "common.", "connect.", "daily.", "feedback.", "goal.", "pause.", "plan.", "profile.", "push.", "readiness.", "rec.", "report.", "resume.", "safety.", "start.", "status.", "symptom.", "sync.", "weekly.", "workout.", "zones." };
        var literal = Regex.Matches(source, "\"([a-z_]+\\.[a-z_.]+)\"").Select(m => m.Groups[1].Value)
            .Where(k => prefixes.Any(k.StartsWith) && !k.EndsWith('.') && k != "plan.infeasible")
            .Distinct();

        string[] dynamic =
        [
            .. new[] { "easy_too_fast", "hr_above_zone", "cut_short", "long_cut_short", "ran_longer" }.Select(f => $"analysis.flag.{f}"),
            .. new[] { "pendingapproval", "active", "paused", "superseded", "rejected", "completed" }.Select(s => $"plan.status.{s}"),
            .. new[] { "no_active_plan", "plan_paused", "not_supported" }.Select(s => $"push.skipped.{s}"),
            .. new[] { "low", "medium", "high" }.Select(s => $"readiness.confidence.{s}"),
            .. new[] { "green", "amber", "red" }.Select(s => $"readiness.level.{s}"),
            .. new[] { "hrv", "resting_hr", "sleep", "recovery", "load_ratio", "soreness", "rpe" }.Select(s => $"readiness.signal.{s}"),
            .. new[] { "approved", "rejected", "undone" }.Select(s => $"rec.{s}"),
            .. new[] { "cardiac", "injury", "illness" }.Select(s => $"safety.{s}"),
            .. Enum.GetNames<CoachBot.Core.Domain.WorkoutType>().Select(t => $"workout.type.{t.ToLowerInvariant()}"),
            .. Enum.GetNames<CoachBot.Core.Domain.WorkoutType>().Select(t => $"workout.rpe.{t.ToLowerInvariant()}"),
            .. new[] { "coros_threshold", "coros_marathon_prediction", "estimated_from_runs", "heart_rate", "rpe" }.Select(s => $"zones.basis.{s}"),
            .. Enum.GetValues<CoachBot.Core.Domain.Intensity>().Select(i => $"intensity.{CoachBot.Core.Planning.PlanDesignMapper.IntensityName(i)}"),
            .. new[] { "5k", "10k", "half", "marathon" }.Select(d => $"race.distance.{d}"),
            .. new[] { "beginner", "intermediate", "advanced" }.Select(l => $"assess.level.{l}"),
            .. new[] { "realistic", "ambitious", "unrealistic" }.Select(v => $"assess.verdict.{v}"),
            .. new[] { "aerobic_base", "long_run_endurance", "threshold", "vo2max", "speed", "race_specific", "consistency", "recovery" }.Select(a => $"assess.limiter.{a}"),
            "help", "disclaimer",
        ];

        var missing = literal.Concat(dynamic).Where(k => !Catalog.Keys("en").Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Missing keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void Renders_structured_workouts_in_both_languages()
    {
        var zones = new CoachBot.Core.Planning.TrainingZones("coros_threshold", 300, 321, 185, 21.0975, 309);
        var blocks = CoachBot.Core.Planning.WorkoutStructure.Resolve(
        [
            new(1, new(CoachBot.Core.Domain.Intensity.Easy, Km: 2)),
            new(5, new(CoachBot.Core.Domain.Intensity.FiveK, Km: 1), new(CoachBot.Core.Domain.Intensity.Recovery, Minutes: 1.5)),
            new(6, new(CoachBot.Core.Domain.Intensity.Strides, Minutes: 0.33)),
        ], zones);
        var fmt = new CoachBot.Core.Services.MessageFormatter(Catalog);

        Assert.Equal("2 km easy → 5 × (1 km 5K pace 4:38–4:50/km, 90 s jog) → 6 × 20 s strides (fast, relaxed)", fmt.Steps("en", blocks));
        Assert.Equal("2 km nhẹ → 5 × (1 km pace 5K 4:38–4:50/km, 90 s chạy hồi phục) → 6 × 20 s strides (tăng tốc ngắn, thả lỏng)", fmt.Steps("vi", blocks));
        Assert.Equal("Bán marathon (21,1 km)", fmt.RaceDistance("vi", 21.0975));
        Assert.Equal("15 km", fmt.RaceDistance("en", 15));
    }

    [Fact]
    public void Formats_arguments_invariantly()
    {
        Assert.Equal("🎯 Goal saved: X (Half marathon) on Sun 14/03, 20 weeks away · target time 1:45:00. Next: /sync full to load your history, then /plan.",
            Catalog.Get("en", "goal.saved", "X", "Half marathon", "Sun 14/03", 20, "1:45:00"));
    }
}

public class TokenCipherTests
{
    private static TokenCipher Cipher() => new(Options.Create(new SecretsOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));

    [Fact]
    public void Round_trips_and_uses_random_nonces()
    {
        var cipher = Cipher();
        var a = cipher.Encrypt("secret-token");
        var b = cipher.Encrypt("secret-token");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("secret-token", a, StringComparison.Ordinal);
        Assert.Equal("secret-token", cipher.Decrypt(a));
    }

    [Fact]
    public void Tampering_is_detected()
    {
        var cipher = Cipher();
        var data = Convert.FromBase64String(cipher.Encrypt("secret-token")[3..]);
        data[15] ^= 0xFF;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => cipher.Decrypt("v1:" + Convert.ToBase64String(data)));
    }

    [Fact]
    public void Rejects_wrong_key_length()
    {
        Assert.Throws<InvalidOperationException>(() => new TokenCipher(Options.Create(new SecretsOptions { EncryptionKey = Convert.ToBase64String(new byte[16]) })));
    }
}

public class CorosOAuthClientTests
{
    private static readonly AuthServerMetadata Server = new("https://auth.example", "https://auth.example/authorize", "https://auth.example/token",
        "https://auth.example/device", "https://auth.example/register", null, null);

    [Fact]
    public async Task Device_polling_handles_pending_and_slow_down()
    {
        var handler = new StubHandler(
            _ => StubHandler.Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            _ => StubHandler.Json(HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
            _ => StubHandler.Json(HttpStatusCode.OK, """{"access_token":"at","refresh_token":"rt","expires_in":3600}"""));
        var client = new CorosOAuthClient(new HttpClient(handler), NullLogger<CorosOAuthClient>.Instance);

        var token = await client.PollDeviceTokenAsync(Server, new OAuthClient("cid", null),
            new DeviceAuthorization("dc", "UC", "https://v", null, 120, 1), "https://mcp.example/mcp", CancellationToken.None);

        Assert.Equal("at", token.AccessToken);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("client_id=cid", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("resource=https%3A%2F%2Fmcp.example%2Fmcp", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_device_login_throws()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.BadRequest, """{"error":"access_denied"}"""));
        var client = new CorosOAuthClient(new HttpClient(handler), NullLogger<CorosOAuthClient>.Instance);

        var ex = await Assert.ThrowsAsync<CorosOAuthException>(() => client.PollDeviceTokenAsync(Server, new OAuthClient("cid", null),
            new DeviceAuthorization("dc", "UC", "https://v", null, 60, 1), "r", CancellationToken.None));
        Assert.Equal("access_denied", ex.Error);
    }

    [Fact]
    public async Task Confidential_client_uses_basic_auth_and_never_sends_secret_in_body()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"access_token":"at2","expires_in":60}"""));
        var client = new CorosOAuthClient(new HttpClient(handler), NullLogger<CorosOAuthClient>.Instance);

        await client.RefreshAsync(Server.TokenEndpoint, new OAuthClient("cid", "s3cret"), "rt", "r", CancellationToken.None);

        var (request, body) = handler.Requests.Single();
        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.DoesNotContain("s3cret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_id", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_returns_server_issued_client()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Created,
            """{"client_id":"abc","token_endpoint_auth_method":"none","grant_types":["authorization_code","refresh_token"]}"""));
        var client = new CorosOAuthClient(new HttpClient(handler), NullLogger<CorosOAuthClient>.Instance);

        var registered = await client.RegisterClientAsync(Server, "CoachBot", ["http://localhost:8080/cb"], ["authorization_code"], "openid", "none", CancellationToken.None);

        Assert.Equal(new OAuthClient("abc", null), registered);
        using var doc = JsonDocument.Parse(handler.Requests.Single().Body!);
        Assert.Equal("none", doc.RootElement.GetProperty("token_endpoint_auth_method").GetString());
    }

    [Fact]
    public void Pkce_challenge_is_s256_of_verifier()
    {
        var (verifier, challenge) = CorosOAuthClient.CreatePkce();
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
        Assert.InRange(verifier.Length, 43, 128);
    }
}

public class OpenAiCompatibleClientTests
{
    private const string Schema = """{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}""";

    private static OpenAiCompatibleLlmClient Client(StubHandler handler, string? baseUrl = "https://llm.example/v1/", string model = "m-1", string? apiKey = "k-123") =>
        new(new HttpClient(handler), Options.Create(new LlmOptions { Provider = "openai_compatible", BaseUrl = baseUrl, Model = model, ApiKey = apiKey }),
            NullLogger<OpenAiCompatibleLlmClient>.Instance);

    [Fact]
    public async Task Sends_schema_constrained_chat_completion_with_bearer_key()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"{\"summary\":\"ok\"}"}}]}"""));

        var json = await Client(handler).GenerateJsonAsync(new LlmRequest("test", "sys", "user", Schema), CancellationToken.None);

        Assert.Equal("""{"summary":"ok"}""", json);
        var (request, body) = handler.Requests.Single();
        Assert.Equal("https://llm.example/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer k-123", request.Headers.Authorization!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("m-1", doc.RootElement.GetProperty("model").GetString());
        var format = doc.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("object", format.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Omits_authorization_without_api_key()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{}"}}]}"""));

        await Client(handler, apiKey: null).GenerateJsonAsync(new LlmRequest("t", "s", "u", Schema), CancellationToken.None);

        Assert.Null(handler.Requests.Single().Request.Headers.Authorization);
    }

    [Fact]
    public async Task Errors_return_null()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"quota"}}"""));
        Assert.Null(await Client(handler).GenerateJsonAsync(new LlmRequest("t", "s", "u", Schema), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, "m-1")]
    [InlineData("https://llm.example/v1", "")]
    public void Disabled_without_base_url_or_model(string? baseUrl, string model) =>
        Assert.False(Client(new StubHandler(), baseUrl, model).IsEnabled);
}

public class PromptLibraryTests
{
    [Theory]
    [InlineData("system")]
    [InlineData("plan_rationale")]
    [InlineData("run_analysis")]
    [InlineData("weekly_report")]
    public void Prompts_are_embedded(string name)
    {
        var prompt = new EmbeddedPromptLibrary().Get(name);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
        if (name != "system")
        {
            Assert.Contains("{{context}}", prompt, StringComparison.Ordinal);
        }
    }
}

public class CorosDiscoveryArgsTests
{
    [Fact]
    public void Builds_dates_limits_and_known_ids_from_schema()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "startDate":{"type":"string","description":"Start date, format yyyyMMdd"},
              "endDate":{"type":"string","description":"yyyyMMdd"},
              "limit":{"type":"integer"},
              "labelId":{"type":"string"},
              "keyword":{"type":["string","null"]},
              "planId":{"type":"string"}},
             "required":["startDate","endDate","labelId","keyword"]}
            """);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));
        var discovery = new CorosDiscovery(null!, Options.Create(new CorosOptions()), time, NullLogger<CorosDiscovery>.Instance);

        var args = discovery.BuildArgs(schema.RootElement, new() { ["labelId"] = "L1" }, out var missing);

        Assert.Empty(missing);
        Assert.Equal("20261014", args["startDate"]);
        Assert.Equal("20261021", args["endDate"]);
        Assert.Equal(5, args["limit"]);
        Assert.Equal("L1", args["labelId"]);
        Assert.Null(args["keyword"]);
        Assert.False(args.ContainsKey("planId"));
    }

    [Fact]
    public void Reports_required_args_it_cannot_fill()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"planId":{"type":"string"}},"required":["planId"]}""");
        var discovery = new CorosDiscovery(null!, Options.Create(new CorosOptions()), TimeProvider.System, NullLogger<CorosDiscovery>.Instance);

        discovery.BuildArgs(schema.RootElement, [], out var missing);

        Assert.Equal(["planId"], missing);
    }
}
