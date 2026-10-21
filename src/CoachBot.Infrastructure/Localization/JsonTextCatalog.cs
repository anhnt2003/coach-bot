using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CoachBot.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CoachBot.Infrastructure.Localization;

/// <summary>
/// User-facing strings from embedded Localization/messages.{lang}.json. Falls back to English, then to the key itself
/// (logged). With <c>strict</c> a missing key throws, which tests use to catch untranslated messages.
/// </summary>
public sealed class JsonTextCatalog : ITextCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> _messages;
    private readonly ILogger<JsonTextCatalog>? _logger;
    private readonly bool _strict;

    public JsonTextCatalog(ILogger<JsonTextCatalog>? logger = null, bool strict = false)
    {
        _logger = logger;
        _strict = strict;
        _messages = Load();
    }

    public IReadOnlyCollection<string> Languages => _messages.Keys;

    public IReadOnlyCollection<string> Keys(string language) => _messages.TryGetValue(language, out var m) ? m.Keys : [];

    public string Get(string language, string key, params object?[] args)
    {
        if (!TryGet(language, key, out var template) && !TryGet("en", key, out template))
        {
            if (_strict)
            {
                throw new KeyNotFoundException($"Missing message key '{key}'.");
            }

            _logger?.LogWarning("Missing message key {Key}", key);
            return key;
        }

        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    private bool TryGet(string language, string key, out string template)
    {
        template = "";
        return _messages.TryGetValue(language, out var m) && m.TryGetValue(key, out template!);
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Localization.messages.", StringComparison.Ordinal)))
        {
            var lang = name.Split('.')[^2];
            using var stream = assembly.GetManifestResourceStream(name)!;
            result[lang] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }

        return result;
    }
}
