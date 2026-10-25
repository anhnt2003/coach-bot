using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
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

    /// <summary>Per attempt. A full plan with structured workouts is a long answer, so allow a few minutes.</summary>
    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 150;
}

public sealed class NullLlmClient : ILlmClient
{
    public bool IsEnabled => false;
    public string ProviderName => "none";
    public Task<string?> GenerateJsonAsync(LlmRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>Any OpenAI-compatible chat completions API (OpenAI, OpenRouter, Ollama...) with JSON-schema constrained output.</summary>
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

        using var response = await http.SendAsync(message, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("LLM {Purpose} failed: {Status} {Body}", request.Purpose, (int)response.StatusCode, text.Length > 400 ? text[..400] : text);
            return null;
        }

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        logger.LogInformation("LLM {Purpose} ok in {Ms} ms", request.Purpose, sw.ElapsedMilliseconds);
        return content;
    }
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
