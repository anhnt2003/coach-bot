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
                if (AllowsNull(prop.Value))
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
}

/// <summary>
/// Tolerant readers for COROS MCP tool output. UNVERIFIED against live responses: field names are candidates taken
/// from COROS documentation and common conventions. Raw payloads are stored alongside mapped values, and
/// `coros discover` captures real responses to confirm or fix these mappings.
/// </summary>
public static class CorosPayload
{
    /// <summary>All objects found in arrays anywhere in the payload (tool outputs usually wrap lists in an envelope).</summary>
    public static IEnumerable<JsonElement> Items(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in payload.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }

                foreach (var nested in Items(item))
                {
                    yield return nested;
                }
            }
        }
        else if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in payload.EnumerateObject())
            {
                foreach (var nested in Items(p.Value))
                {
                    yield return nested;
                }
            }
        }
    }

    public static JsonElement? Field(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in obj.EnumerateObject())
        {
            if (names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return p.Value;
            }
        }

        return null;
    }

    public static double? Number(JsonElement obj, params string[] names)
    {
        var v = Field(obj, names);
        return v?.ValueKind switch
        {
            JsonValueKind.Number => v.Value.GetDouble(),
            JsonValueKind.String when double.TryParse(v.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static string? Text(JsonElement obj, params string[] names)
    {
        var v = Field(obj, names);
        return v?.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Accepts yyyyMMdd (number or string), yyyy-MM-dd, or an ISO timestamp.</summary>
    public static DateOnly? Date(JsonElement obj, params string[] names)
    {
        var raw = Text(obj, names);
        if (raw is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
        {
            return compact;
        }

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? DateOnly.FromDateTime(ts.Date) : null;
    }

    /// <summary>Accepts epoch seconds, epoch milliseconds or an ISO-8601 string.</summary>
    public static DateTimeOffset? Instant(JsonElement obj, params string[] names)
    {
        var v = Field(obj, names);
        if (v is null)
        {
            return null;
        }

        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt64(out var epoch))
        {
            return epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        var s = v.Value.GetString();
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
        {
            return e > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(e) : DateTimeOffset.FromUnixTimeSeconds(e);
        }

        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? ts.ToUniversalTime() : null;
    }

    /// <summary>Pace as seconds per km, from a number or "m:ss" text.</summary>
    public static int? PaceSeconds(JsonElement obj, params string[] names)
    {
        var raw = Text(obj, names);
        if (raw is null)
        {
            return null;
        }

        var parts = raw.Split(':', '\'', '"').Where(p => p.Length > 0).ToArray();
        if (parts.Length >= 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var m) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var sec))
        {
            return m * 60 + sec;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n is > 120 and < 1200 ? (int)Math.Round(n) : null;
    }

    /// <summary>Structure only (key names), for logs that must not contain health values.</summary>
    public static string Shape(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().Take(15).Select(p => p.Name + (p.Value.ValueKind == JsonValueKind.Array ? $"[{p.Value.GetArrayLength()}]" : ""))) + "}",
        JsonValueKind.Array => $"[{e.GetArrayLength()}]",
        _ => e.ValueKind.ToString(),
    };
}
