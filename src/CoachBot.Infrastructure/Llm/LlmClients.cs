using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoachBot.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>none | openai_compatible</summary>
    [RegularExpression("^(none|openai_compatible)$")]
    public string Provider { get; set; } = "none";

    /// <summary>Model id exactly as the provider expects it.</summary>
    public string Model { get; set; } = "";

    public string? ApiKey { get; set; }

    /// <summary>Chat completions base URL, e.g. https://api.openai.com/v1 or http://localhost:11434/v1.</summary>
    public string? BaseUrl { get; set; }

    [Range(0, 2)]
    public double Temperature { get; set; } = 0.4;

    /// <summary>Per attempt: the wait for the response headers of the streamed answer.</summary>
    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 150;

    /// <summary>
    /// Whole call, retries included: the resilience handler's total timeout and the budget for a streamed answer.
    /// A reasoning model can need 10+ minutes for a full plan.
    /// </summary>
    [Range(30, 1800)]
    public int TotalTimeoutSeconds { get; set; } = 900;

    public TimeSpan TotalTimeout => TimeSpan.FromSeconds(TotalTimeoutSeconds);
}

public sealed class NullLlmClient : ILlmClient
{
    public bool IsEnabled => false;
    public string ProviderName => "none";
    public Task<string?> GenerateJsonAsync(LlmRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// Any OpenAI-compatible chat completions API (OpenAI, OpenRouter, Ollama...) with JSON-schema constrained output.
/// The answer is streamed: a reasoning model can think for minutes, and proxies behind Cloudflare cut a request whose
/// answer has not started after ~100 s (HTTP 524). A server that ignores "stream" and answers with plain JSON works too.
/// </summary>
public sealed class OpenAiCompatibleLlmClient(HttpClient http, IOptions<LlmOptions> options, ILogger<OpenAiCompatibleLlmClient> logger) : ILlmClient
{
    public bool IsEnabled => !string.IsNullOrWhiteSpace(options.Value.BaseUrl) && !string.IsNullOrWhiteSpace(options.Value.Model);
    public string ProviderName => $"openai_compatible:{options.Value.Model}";

    public async Task<string?> GenerateJsonAsync(LlmRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Value.Model,
            ["temperature"] = options.Value.Temperature,
            ["stream"] = true,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = request.Purpose, ["schema"] = JsonNode.Parse(request.JsonSchema) },
            },
        };

        var sw = Stopwatch.StartNew();
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.BaseUrl!.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            message.Headers.Authorization = new("Bearer", options.Value.ApiKey);
        }

        // The resilience handler only times the wait for the response headers; the streamed body shares its total budget.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.Value.TotalTimeout);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(deadline.Token);
            logger.LogWarning("LLM {Purpose} failed: {Status} {Body}", request.Purpose, (int)response.StatusCode, text.Length > 400 ? text[..400] : text);
            return null;
        }

        var (content, finish) = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? await ReadStreamAsync(response.Content, deadline.Token)
            : ReadMessage(await response.Content.ReadAsStringAsync(deadline.Token));
        if (string.IsNullOrEmpty(content))
        {
            logger.LogWarning("LLM {Purpose} returned no content (finish={Finish})", request.Purpose, finish);
            return null;
        }

        logger.LogInformation("LLM {Purpose} ok in {Ms} ms (finish={Finish})", request.Purpose, sw.ElapsedMilliseconds, finish);
        return content;
    }

    private static (string? Content, string? Finish) ReadMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0];
        return (choice.GetProperty("message").GetProperty("content").GetString(),
            choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null);
    }

    /// <summary>Joins the content deltas of a server-sent event stream. Comment lines (proxy keep-alives) are skipped.</summary>
    private static async Task<(string? Content, string? Finish)> ReadStreamAsync(HttpContent body, CancellationToken ct)
    {
        var content = new StringBuilder();
        string? finish = null;
        using var reader = new StreamReader(await body.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            using var chunk = JsonDocument.Parse(data);
            if (chunk.RootElement.TryGetProperty("error", out var error))
            {
                throw new HttpRequestException($"LLM stream error: {Truncate(error.ToString())}");
            }

            if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            if (choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
            {
                content.Append(text.GetString());
            }

            if (choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                finish = reason.GetString();
            }
        }

        return (content.ToString(), finish);
    }

    private static string Truncate(string s) => s.Length > 400 ? s[..400] : s;
}

/// <summary>Prompt templates embedded from Llm/Prompts/*.md.</summary>
public sealed class EmbeddedPromptLibrary : IPromptLibrary
{
    private readonly Dictionary<string, string> _prompts;

    public EmbeddedPromptLibrary()
    {
        var assembly = Assembly.GetExecutingAssembly();
        _prompts = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Llm.Prompts.", StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .ToDictionary(
                n => n[(n.IndexOf(".Llm.Prompts.", StringComparison.Ordinal) + ".Llm.Prompts.".Length)..^3],
                n =>
                {
                    using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                    return reader.ReadToEnd();
                });
    }

    public string Get(string name) =>
        _prompts.TryGetValue(name, out var prompt) ? prompt : throw new KeyNotFoundException($"Prompt '{name}' is not embedded.");
}
