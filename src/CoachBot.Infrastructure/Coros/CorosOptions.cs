using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure.Coros;

public sealed class CorosOptions
{
    public const string Section = "Coros";

    /// <summary>"mock" (synthetic data, default) or "mcp" (official COROS MCP server).</summary>
    [RegularExpression("^(mock|mcp)$")]
    public string Mode { get; set; } = "mock";

    /// <summary>Regional endpoint: mcp (global), mcpeu, mcpus or mcpcn .coros.com/mcp.</summary>
    [Required]
    public string McpUrl { get; set; } = "https://mcp.coros.com/mcp";

    [RegularExpression("^(normal|fatigued|missing_hrv|sparse_history)$")]
    public string MockScenario { get; set; } = "normal";

    /// <summary>
    /// Automatic pushing of approved workouts to the COROS calendar. Off by default: enable only after
    /// `coros test-write` has created a workout you could see (and delete) in the COROS app.
    /// </summary>
    public bool EnableWorkoutPush { get; set; }

    public string ClientName { get; set; } = "CoachBot personal running coach";
    public string Scopes { get; set; } = "openid mcp.tools offline_access";
    public int ToolTimeoutSeconds { get; set; } = 60;

    /// <summary>Where `coros discover` writes raw tool schemas/responses (personal data; gitignored).</summary>
    public string FixturesDirectory { get; set; } = ".local/coros-fixtures";
}

public sealed class SecretsOptions
{
    public const string Section = "Secrets";

    /// <summary>Base64 of 32 random bytes; encrypts COROS tokens at rest. Generate: openssl rand -base64 32</summary>
    [Required]
    public string EncryptionKey { get; set; } = "";
}

/// <summary>AES-256-GCM for OAuth tokens stored in the database.</summary>
public sealed class TokenCipher
{
    private const string Prefix = "v1:";
    private readonly byte[] _key;

    public TokenCipher(IOptions<SecretsOptions> options)
    {
        try
        {
            _key = Convert.FromBase64String(options.Value.EncryptionKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Secrets__EncryptionKey must be base64 (generate with: openssl rand -base64 32).");
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Secrets__EncryptionKey must decode to exactly 32 bytes.");
        }
    }

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Decrypt(string encrypted)
    {
        if (!encrypted.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown token format.");
        }

        var data = Convert.FromBase64String(encrypted[Prefix.Length..]);
        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(data.Length - 16);
        var cipher = data.AsSpan(12, data.Length - 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
