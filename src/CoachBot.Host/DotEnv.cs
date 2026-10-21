namespace CoachBot.Host;

/// <summary>
/// Minimal .env loader for local runs. Real environment variables always win, so Docker/hosting configuration is
/// never overridden by a stray file.
/// </summary>
internal static class DotEnv
{
    /// <summary>Loads the nearest .env from <paramref name="startDirectory"/> or its parents (`dotnet run` starts in the project folder).</summary>
    public static void LoadNearest(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
            {
                Load(candidate);
                return;
            }
        }
    }

    public static void Load(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
