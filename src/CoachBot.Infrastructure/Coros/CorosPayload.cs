using System.Globalization;
using System.Text.Json;

namespace CoachBot.Infrastructure.Coros;

/// <summary>
/// Schema-driven argument building for COROS MCP tools: values come from the live input schema (tools/list), so
/// parameter names and date formats follow what the server actually declares.
/// </summary>
public static class CorosToolArgs
{
    private static readonly object Missing = new();

    /// <param name="desired">Values by lower-cased semantic name: startdate/enddate (DateOnly), days, limit, or exact property names.</param>
    public static Dictionary<string, object?> Build(JsonElement schema, IReadOnlyDictionary<string, object?> desired, out List<string> missing)
    {
        missing = [];
        var args = new Dictionary<string, object?>();
        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString()!).ToHashSet()
            : [];
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
        {
            return args;
        }

        foreach (var prop in props.EnumerateObject())
        {
            var value = Resolve(prop.Name, prop.Value, desired);
            if (value != Missing)
            {
                args[prop.Name] = value;
            }
            else if (required.Contains(prop.Name))
            {
                // COROS lists every filter as required (strict-schema style) but describes unused ones as "Optional":
                // those are sent as null.
                if (AllowsNull(prop.Value) || IsDeclaredOptional(prop.Value))
                {
                    args[prop.Name] = null;
                }
                else
                {
                    missing.Add(prop.Name);
                }
            }
        }

        return args;
    }

    private static object? Resolve(string name, JsonElement prop, IReadOnlyDictionary<string, object?> desired)
    {
        if (!desired.TryGetValue(name, out var value))
        {
            var lower = name.ToLowerInvariant();
            var semantic = lower switch
            {
                "startdate" or "startday" => "startdate",
                "enddate" or "endday" => "enddate",
                _ => lower,
            };

            if (!desired.TryGetValue(semantic, out value))
            {
                return Missing;
            }
        }

        // Exact name matches too: a schema property named "startdate" still needs its declared date format.
        return value is DateOnly date ? FormatDate(date, prop) : value;
    }

    public static string FormatDate(DateOnly date, JsonElement prop)
    {
        var hint = prop.ToString();
        var compact = hint.Contains("yyyyMMdd", StringComparison.OrdinalIgnoreCase) || hint.Contains(@"\\d{8}", StringComparison.Ordinal);
        return date.ToString(compact ? "yyyyMMdd" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static bool AllowsNull(JsonElement prop) =>
        prop.TryGetProperty("type", out var t) &&
        (t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Any(x => x.GetString() == "null") : t.GetString() == "null");

    private static bool IsDeclaredOptional(JsonElement prop) =>
        prop.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
        && d.GetString()!.TrimStart().StartsWith("Optional", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Log helpers for COROS MCP payloads.</summary>
public static class CorosPayload
{
    /// <summary>Structure only (key names), for logs that must not contain health values.</summary>
    public static string Shape(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().Take(15).Select(p => p.Name + (p.Value.ValueKind == JsonValueKind.Array ? $"[{p.Value.GetArrayLength()}]" : ""))) + "}",
        JsonValueKind.Array => $"[{e.GetArrayLength()}]",
        _ => e.ValueKind.ToString(),
    };
}
