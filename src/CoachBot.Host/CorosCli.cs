using System.Globalization;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Services;
using CoachBot.Infrastructure.Coros;
using Microsoft.EntityFrameworkCore;

namespace CoachBot.Host;

/// <summary>
/// `dotnet run -- coros login|discover|status|test-write yyyy-MM-dd`: one-off commands for connecting and verifying
/// the real COROS integration from a terminal.
/// </summary>
internal static class CorosCli
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var athlete = await sp.GetRequiredService<AthleteService>().GetOrCreatePrimaryAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        switch (args.FirstOrDefault())
        {
            case "login":
                return await LoginAsync(sp, athlete.Id, cts.Token);
            case "discover":
                var dir = await sp.GetRequiredService<CorosDiscovery>().RunAsync(athlete.Id, Console.Out, cts.Token);
                Console.WriteLine($"Raw responses written to {dir} (personal data; do not commit).");
                return 0;
            case "status":
                return await StatusAsync(sp, athlete.Id, cts.Token);
            case "test-write" when args.Length > 1 && DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                return await TestWriteAsync(sp, athlete.Id, date, cts.Token);
            default:
                Console.Error.WriteLine("Usage: coros login | coros discover | coros status | coros test-write yyyy-MM-dd");
                return 2;
        }
    }

    private static async Task<int> LoginAsync(IServiceProvider sp, Guid athleteId, CancellationToken ct)
    {
        var connect = sp.GetRequiredService<CorosConnectService>();
        try
        {
            var started = await connect.StartDeviceLoginAsync(athleteId, ct);
            Console.WriteLine();
            Console.WriteLine("Open this page and approve access to your COROS account:");
            Console.WriteLine($"  {started.VerificationUriComplete ?? started.VerificationUri}");
            Console.WriteLine($"  Code: {started.UserCode}   (expires in {started.ExpiresInSeconds / 60} min)");
            Console.WriteLine("Waiting for approval...");
            await connect.CompleteDeviceLoginAsync(athleteId, started.Context, ct);
        }
        catch (Exception ex) when (ex is CorosOAuthException or InvalidOperationException)
        {
            // Verified live: COROS does not grant the device flow to dynamically registered clients.
            Console.WriteLine($"Device login unavailable ({ex.Message}); using browser login (PKCE).");
            if (!await BrowserLoginAsync(connect, athleteId, ct))
            {
                return 1;
            }
        }

        Console.WriteLine("Authorized. Verifying tools/list ...");
        var caps = await sp.GetRequiredService<CorosMcpGateway>().GetCapabilitiesAsync(athleteId, ct);
        Console.WriteLine($"COROS MCP reachable: {caps.Tools.Count} tools. Required read tools present: {caps.CanRead}. " +
                          $"Workout tools present: create={caps.Tools.Contains("createScheduledWorkout")}, update={caps.Tools.Contains("updateScheduledWorkout")}. " +
                          $"Automatic push enabled: {caps.CanPushWorkouts}.");
        Console.WriteLine("Next: `coros discover` to record real tool outputs, then `coros test-write yyyy-MM-dd`.");
        return 0;
    }

    /// <summary>
    /// Opens a one-shot listener on the registered localhost redirect URI. If it cannot listen (port busy or a non-local
    /// redirect), the athlete pastes the URL the browser was redirected to.
    /// </summary>
    private static async Task<bool> BrowserLoginAsync(CorosConnectService connect, Guid athleteId, CancellationToken ct)
    {
        var authorizeUrl = await connect.StartBrowserLoginAsync(athleteId, ct);
        var redirect = new Uri(connect.RedirectUri);
        Console.WriteLine();
        Console.WriteLine("Open this link in a browser on THIS computer, sign in to COROS and approve access:");
        Console.WriteLine($"  {authorizeUrl}");

        if (redirect.IsLoopback)
        {
            using var listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"{redirect.GetLeftPart(UriPartial.Authority)}{redirect.AbsolutePath.TrimEnd('/')}/");
            try
            {
                listener.Start();
                Console.WriteLine($"Waiting for the redirect on {redirect} (10 min)...");
                var contextTask = listener.GetContextAsync();
                if (await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(10), ct)) != contextTask)
                {
                    Console.Error.WriteLine("Timed out waiting for approval.");
                    return false;
                }

                var context = await contextTask;
                var ok = await connect.CompleteFromRedirectUrlAsync(context.Request.Url!.ToString(), ct);
                var html = System.Text.Encoding.UTF8.GetBytes(ok
                    ? "<h3>COROS connected to CoachBot.</h3>You can close this tab."
                    : "<h3>COROS authorization failed.</h3>Check the terminal.");
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(html, ct);
                context.Response.Close();
                if (!ok)
                {
                    Console.Error.WriteLine($"Authorization failed: {context.Request.Url!.Query}");
                }

                return ok;
            }
            catch (System.Net.HttpListenerException ex)
            {
                Console.WriteLine($"Could not listen on {redirect} ({ex.Message}).");
            }
        }

        Console.WriteLine("After approving, paste the full URL from the browser's address bar here and press Enter:");
        var pasted = Console.ReadLine();
        return pasted is not null && await connect.CompleteFromRedirectUrlAsync(pasted, ct);
    }

    private static async Task<int> StatusAsync(IServiceProvider sp, Guid athleteId, CancellationToken ct)
    {
        var c = await sp.GetRequiredService<ICoachDb>().CorosConnections.AsNoTracking().FirstOrDefaultAsync(x => x.AthleteId == athleteId, ct);
        if (c is null)
        {
            Console.WriteLine("Not connected.");
            return 1;
        }

        Console.WriteLine($"MCP: {c.McpUrl}\nIssuer: {c.Issuer}\nAccess token: {(c.EncryptedAccessToken is null ? "none" : $"expires {c.AccessTokenExpiresAt:u}")}" +
                          $"\nRefresh token: {(c.EncryptedRefreshToken is null ? "none" : "stored")}\nLast verified: {c.LastVerifiedAt:u}\nLast sync: {c.LastSyncAt:u} {c.LastSyncError}");
        return 0;
    }

    private static async Task<int> TestWriteAsync(IServiceProvider sp, Guid athleteId, DateOnly date, CancellationToken ct)
    {
        var gateway = sp.GetRequiredService<CorosMcpGateway>();
        var workout = new CoachBot.Core.Domain.PlannedWorkout
        {
            Date = date,
            Type = CoachBot.Core.Domain.WorkoutType.Easy,
            TargetDistanceKm = 5,
            TargetDurationMinutes = 35,
            Title = "CoachBot test – easy 5 km (safe to delete)",
            Description = "Created by `coros test-write` to verify the COROS integration. Delete it in the COROS app.",
        };
        var result = await gateway.PushScheduledWorkoutAsync(athleteId, workout, ct);
        Console.WriteLine(result.Success
            ? $"Created on {date:yyyy-MM-dd} (ref {result.ExternalRef}). Check the COROS app calendar, then delete it there (the MCP tools cannot delete)."
            : $"Write failed: {result.Error}");
        return result.Success ? 0 : 1;
    }
}
