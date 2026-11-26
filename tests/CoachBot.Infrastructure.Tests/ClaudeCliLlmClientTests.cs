using CoachBot.Core.Abstractions;
using CoachBot.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CoachBot.Infrastructure.Tests;

public class ClaudeCliLlmClientTests
{
    private const string Schema = """{ "type": "object", "properties": { "summary": { "type": "string" } }, "required": ["summary"] }""";

    // Shape of `claude -p --output-format json --json-schema ...` (Claude Code 2.1), trimmed.
    private const string Success = """
        {"type":"result","subtype":"success","is_error":false,"num_turns":2,"terminal_reason":"completed","api_error_status":null,
         "result":"{\"summary\":\"ok\"}","structured_output":{"summary":"ok"},"credential_expires_at":"2027-11-26T08:00:00.000Z"}
        """;

    private static LlmRequest Request(string user = "user prompt") => new("test", "system prompt", user, Schema);

    private static LlmOptions Options(string path = "claude", int totalTimeoutSeconds = 900) =>
        new() { Provider = LlmOptions.ClaudeCli, Model = "opus", ClaudePath = path, TotalTimeoutSeconds = totalTimeoutSeconds };

    private static ClaudeCliLlmClient Client(LlmOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), new FakeTimeProvider(DateTimeOffset.Parse("2026-11-26T00:00:00Z")),
            NullLogger<ClaudeCliLlmClient>.Instance);

    [Fact]
    public void Returns_the_structured_output_and_the_sign_in_expiry()
    {
        var json = ClaudeCliLlmClient.ReadResult("test", 0, Success, "", out var expires);

        Assert.Equal("""{"summary":"ok"}""", json);
        Assert.Equal(DateTimeOffset.Parse("2027-11-26T08:00:00Z"), expires);
    }

    [Fact]
    public void An_api_error_fails_with_its_status_and_message()
    {
        const string error = """
            {"type":"result","subtype":"success","is_error":true,"api_error_status":404,"terminal_reason":"api_error",
             "result":"There's an issue with the selected model (nope)."}
            """;

        var ex = Assert.Throws<LlmException>(() => ClaudeCliLlmClient.ReadResult("test", 1, error, "", out _));

        Assert.Equal(LlmFailure.Http, ex.Reason);
        Assert.Contains("HTTP 404", ex.Detail, StringComparison.Ordinal);
        Assert.Contains("selected model", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_without_structured_output_is_empty()
    {
        const string declined = """{"type":"result","subtype":"error_max_turns","is_error":false,"terminal_reason":"max_turns","result":"I can't."}""";

        var ex = Assert.Throws<LlmException>(() => ClaudeCliLlmClient.ReadResult("test", 0, declined, "", out _));

        Assert.Equal(LlmFailure.Empty, ex.Reason);
        Assert.Contains("error_max_turns", ex.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Error: --json-schema is not a valid JSON Schema: data/type must be array", "")]
    [InlineData("", "Not logged in · Please run /login")]
    public void Output_that_is_not_a_result_fails_with_the_cli_message(string stdout, string stderr)
    {
        var ex = Assert.Throws<LlmException>(() => ClaudeCliLlmClient.ReadResult("test", 1, stdout, stderr, out _));

        Assert.Equal(LlmFailure.Http, ex.Reason);
        Assert.Contains("exited with 1", ex.Detail, StringComparison.Ordinal);
        Assert.Contains((stdout + stderr).Split(':')[0], ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Runs_print_mode_without_tools_settings_or_sessions()
    {
        var args = ClaudeCliLlmClient.Arguments(Options(), Request()).ToList();

        Assert.Equal("-p", args[0]);
        Assert.Equal("json", After("--output-format"));
        Assert.Equal("""{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}""", After("--json-schema"));
        Assert.Equal("system prompt", After("--system-prompt"));
        Assert.Equal("opus", After("--model"));
        Assert.Equal("", After("--tools"));
        Assert.Contains("--safe-mode", args);
        Assert.Contains("--restricted", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain("--bare", args); // bare mode ignores subscription sign-in
        Assert.DoesNotContain("--effort", args);
        Assert.DoesNotContain("user prompt", args); // goes through stdin

        var withEffort = Options();
        withEffort.Effort = "high";
        Assert.Equal(["--effort", "high"], ClaudeCliLlmClient.Arguments(withEffort, Request()).TakeLast(2));

        string After(string flag) => args[args.IndexOf(flag) + 1];
    }

    [Fact]
    public async Task Sends_the_prompt_through_stdin_and_returns_the_answer()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var stdin = Path.Combine(dir.Path, "stdin.txt");
        var cli = dir.Script($"""
            cat > '{stdin}'
            cat <<'EOF'
            {Success.ReplaceLineEndings(" ")}
            EOF
            """);

        var json = await Client(Options(cli)).GenerateJsonAsync(Request("Xin chào, context {\"km\": 10}"), CancellationToken.None);

        Assert.Equal("""{"summary":"ok"}""", json);
        Assert.Equal("Xin chào, context {\"km\": 10}", await File.ReadAllTextAsync(stdin));
    }

    [Fact]
    public async Task A_cli_that_does_not_answer_in_time_is_stopped()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var cli = dir.Script("cat > /dev/null; exec sleep 30");

        var started = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<LlmException>(() => Client(Options(cli, totalTimeoutSeconds: 1)).GenerateJsonAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmFailure.Timeout, ex.Reason);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_missing_cli_fails_as_unreachable()
    {
        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            Client(Options("/nonexistent/claude")).GenerateJsonAsync(Request(), CancellationToken.None));

        Assert.Equal(LlmFailure.Http, ex.Reason);
        Assert.Contains("cannot start", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_cli_needs_no_base_url_and_replaces_the_http_client()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=x",
            ["Llm:Provider"] = "claude_cli",
            ["Llm:Model"] = "opus",
        }).Build();
        using var sp = new ServiceCollection().AddLogging().AddCoachBotCore(config).BuildServiceProvider();

        Assert.Equal("opus", sp.GetRequiredService<IOptions<LlmOptions>>().Value.Model);
        Assert.Equal("claude_cli:opus", Assert.IsType<ClaudeCliLlmClient>(sp.GetRequiredService<ILlmClient>()).ProviderName);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("coachbot-cli-test").FullName;

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        public string Script(string body)
        {
            var file = System.IO.Path.Combine(Path, "claude");
            File.WriteAllText(file, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
