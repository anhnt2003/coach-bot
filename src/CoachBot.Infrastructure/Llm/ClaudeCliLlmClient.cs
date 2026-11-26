using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoachBot.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure.Llm;

/// <summary>
/// Runs the installed, unmodified Claude Code CLI in print mode, signed in with the user's own Claude subscription
/// (`claude login` on the machine, or a `claude setup-token` token in CLAUDE_CODE_OAUTH_TOKEN). The CLI holds the
/// credentials; this client never reads them. Tools, MCP servers, hooks, CLAUDE.md and settings files are off, and it
/// runs in an empty folder, so the model only sees the prompt. The answer is the CLI's validated structured output.
/// </summary>
public sealed class ClaudeCliLlmClient(IOptions<LlmOptions> options, TimeProvider time, ILogger<ClaudeCliLlmClient> logger) : ILlmClient
{
    /// <summary>Warn this long before the sign-in the CLI uses expires: an unattended bot has nobody to notice.</summary>
    private static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);

    private static readonly string WorkDirectory = Path.Combine(Path.GetTempPath(), "coachbot-claude-cli");

    public string ProviderName => $"claude_cli:{options.Value.Model}";

    public async Task<string> GenerateJsonAsync(LlmRequest request, CancellationToken ct)
    {
        var o = options.Value;
        Directory.CreateDirectory(WorkDirectory);
        var start = new ProcessStartInfo(o.ClaudePath)
        {
            WorkingDirectory = WorkDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in Arguments(o, request))
        {
            start.ArgumentList.Add(arg);
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new LlmException(request.Purpose, LlmFailure.Http, $"cannot start '{o.ClaudePath}': {ex.Message}", ex);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(o.TotalTimeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                // The prompt goes through stdin: the context can be larger than a command line allows.
                await process.StandardInput.WriteAsync(request.UserPrompt.AsMemory(), deadline.Token);
            }
            catch (IOException)
            {
                // The CLI exited before reading the prompt; its output says why.
            }
            finally
            {
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(deadline.Token);
            var content = ReadResult(request.Purpose, process.ExitCode, await stdout, await stderr, out var credentialExpiresAt);
            WarnIfSignInExpires(credentialExpiresAt);
            logger.LogInformation("LLM {Purpose} ok in {Ms} ms (claude_cli)", request.Purpose, sw.ElapsedMilliseconds);
            return content;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Kill(process);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Kill(process);
            throw new LlmException(request.Purpose, LlmFailure.Timeout, $"no complete answer within {o.TotalTimeoutSeconds} s", ex);
        }
    }

    internal static IEnumerable<string> Arguments(LlmOptions o, LlmRequest request)
    {
        string[] args =
        [
            "-p",
            "--output-format", "json",
            "--json-schema", JsonNode.Parse(request.JsonSchema)!.ToJsonString(),
            "--system-prompt", request.SystemPrompt,
            "--model", o.Model,
            // No tools, settings files, MCP servers, hooks, plugins or CLAUDE.md: the prompt is the whole input.
            "--tools", "",
            "--safe-mode",
            "--restricted",
            "--strict-mcp-config",
            "--no-session-persistence",
        ];
        return string.IsNullOrEmpty(o.Effort) ? args : [.. args, "--effort", o.Effort];
    }

    /// <summary>Returns the structured output of a print-mode JSON result, or throws <see cref="LlmException"/>.</summary>
    internal static string ReadResult(string purpose, int exitCode, string stdout, string stderr, out DateTimeOffset? credentialExpiresAt)
    {
        credentialExpiresAt = null;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            // Not a result object, e.g. "Error: --json-schema is not a valid JSON Schema" or a sign-in message.
        }

        using (doc)
        {
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || type.GetString() != "result")
            {
                var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
                throw new LlmException(purpose, LlmFailure.Http, $"claude exited with {exitCode}: {Truncate(output.Trim())}");
            }

            var root = doc.RootElement;
            if (root.TryGetProperty("credential_expires_at", out var expires) && expires.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                credentialExpiresAt = at;
            }

            var result = Text(root, "result");
            if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
            {
                var status = root.TryGetProperty("api_error_status", out var s) && s.ValueKind == JsonValueKind.Number ? $" (HTTP {s.GetInt32()})" : "";
                throw new LlmException(purpose, LlmFailure.Http, $"claude error{status}: {Truncate(result ?? Text(root, "subtype") ?? "unknown")}");
            }

            if (!root.TryGetProperty("structured_output", out var structured) || structured.ValueKind != JsonValueKind.Object)
            {
                // E.g. the model declined, or the run stopped before a valid answer.
                throw new LlmException(purpose, LlmFailure.Empty,
                    $"no structured output (subtype={Text(root, "subtype") ?? "none"}, terminal_reason={Text(root, "terminal_reason") ?? "none"}): {Truncate(result ?? "")}");
            }

            return structured.GetRawText();
        }
    }

    private void WarnIfSignInExpires(DateTimeOffset? expiresAt)
    {
        if (expiresAt is { } at && at - time.GetUtcNow() < ExpiryWarning)
        {
            logger.LogWarning("The Claude sign-in used by the claude CLI expires at {ExpiresAt:u}: renew it (claude setup-token or claude login)", at);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Truncate(string s) => s.Length > 400 ? s[..400] : s;
}
