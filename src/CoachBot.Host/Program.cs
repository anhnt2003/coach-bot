using CoachBot.Host;
using CoachBot.Infrastructure;
using CoachBot.Infrastructure.Coros;
using CoachBot.Infrastructure.Persistence;

if (args is ["health"])
{
    // Container healthcheck (the runtime image has no curl/wget).
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        return (await http.GetAsync(new Uri("http://127.0.0.1:8080/health"))).IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
}

DotEnv.LoadNearest(Directory.GetCurrentDirectory());

// No config hot-reload: it is not needed, and on macOS the file watcher it starts calls sync(), which blocks
// indefinitely when any mounted network share is unresponsive.
Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");

var isCli = args.Length > 0 && args[0] == "coros";
// Content root = the app folder, so appsettings.json is found regardless of the working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

builder.Logging.ClearProviders();
if (builder.Environment.IsProduction() && !isCli)
{
    builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
}
else
{
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
}

builder.Services.AddCoachBotCore(builder.Configuration);
if (!isCli)
{
    builder.Services.AddCoachBotRuntime(builder.Configuration);
}

var app = builder.Build();

if (isCli)
{
    return await CorosCli.RunAsync(app.Services, args[1..]);
}

app.MapGet("/health", async (CoachDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.Problem("database unreachable", statusCode: 503));

// PKCE fallback for COROS login (only used when the device flow is unavailable).
app.MapGet("/oauth/coros/callback", async (string? code, string? state, string? error, CorosConnectService connect, CancellationToken ct) =>
{
    if (error is not null || code is null || state is null)
    {
        return Results.Text($"COROS authorization failed: {error ?? "missing code"}", statusCode: 400);
    }

    return await connect.CompleteBrowserLoginAsync(state, code, ct)
        ? Results.Text("COROS connected. You can close this tab and return to Telegram.")
        : Results.Text("Login session expired or unknown. Start again with /connect.", statusCode: 400);
});

await app.RunAsync();
return 0;
