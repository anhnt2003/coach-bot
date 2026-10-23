using CoachBot.Core.Abstractions;
using CoachBot.Core.Services;
using CoachBot.Infrastructure.Coros;
using CoachBot.Infrastructure.Llm;
using CoachBot.Infrastructure.Localization;
using CoachBot.Infrastructure.Persistence;
using CoachBot.Infrastructure.Scheduling;
using CoachBot.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoachBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCoachBotCore(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<CoachOptions>().Bind(config.GetSection(CoachOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SafetyOptions>().Bind(config.GetSection(SafetyOptions.Section));
        services.AddOptions<CorosOptions>().Bind(config.GetSection(CorosOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SecretsOptions>().Bind(config.GetSection(SecretsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.TryAddTimeProvider();

        var connectionString = config.GetConnectionString("Postgres")
                               ?? throw new InvalidOperationException("ConnectionStrings__Postgres is not set.");
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("GSS Encryption Mode", StringComparison.OrdinalIgnoreCase))
        {
            // Supabase does not use Kerberos; skipping GSS negotiation avoids loading libgssapi (absent in slim images).
            csb.GssEncryptionMode = Npgsql.GssEncryptionMode.Disable;
        }

        connectionString = csb.ConnectionString;
        services.AddDbContext<CoachDbContext>(o => o
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<ICoachDb>(sp => sp.GetRequiredService<CoachDbContext>());

        // Application services (Core).
        services.AddScoped<AthleteService>();
        services.AddScoped<AlertService>();
        services.AddScoped<SyncService>();
        services.AddScoped<PlanService>();
        services.AddScoped<ReadinessService>();
        services.AddScoped<RecommendationService>();
        services.AddScoped<AnalysisService>();
        services.AddScoped<FeedbackService>();
        services.AddScoped<ReportService>();
        services.AddScoped<CorosPushService>();
        services.AddScoped<CoachLlm>();
        services.AddSingleton<MessageFormatter>();

        AddCoros(services);
        AddLlm(services, config);
        services.AddSingleton<ITextCatalog>(sp => new JsonTextCatalog(sp.GetService<Microsoft.Extensions.Logging.ILogger<JsonTextCatalog>>()));

        services.AddOptions<TelegramOptions>().Bind(config.GetSection(TelegramOptions.Section));
        services.AddHttpClient("telegram", c => c.Timeout = TimeSpan.FromSeconds(100));
        services.AddSingleton<TelegramClientHolder>();
        services.AddScoped<INotifier, TelegramNotifier>();
        return services;
    }

    /// <summary>Long-running parts: Telegram bot and scheduled jobs. Not needed for CLI commands.</summary>
    public static IServiceCollection AddCoachBotRuntime(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<JobsOptions>().Bind(config.GetSection(JobsOptions.Section));
        services.AddScoped<TelegramUpdateHandler>();
        services.AddHostedService<StartupChecks>();
        services.AddHostedService<TelegramBotService>();
        services.AddSingleton<CronJobService>();
        services.AddHostedService(sp => sp.GetRequiredService<CronJobService>());
        return services;
    }

    private static void AddLlm(IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<LlmOptions>().Bind(config.GetSection(LlmOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IPromptLibrary, EmbeddedPromptLibrary>();

        var timeout = int.TryParse(config.GetSection(LlmOptions.Section)["TimeoutSeconds"], out var t) ? t : new LlmOptions().TimeoutSeconds;
        void Resilience(IHttpClientBuilder b) => b.AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(timeout);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(timeout * 2.5);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(timeout * 2.5);
            o.Retry.MaxRetryAttempts = 2;
        });

        Resilience(services.AddHttpClient<GeminiLlmClient>(c => c.Timeout = Timeout.InfiniteTimeSpan));
        Resilience(services.AddHttpClient<OpenAiCompatibleLlmClient>(c => c.Timeout = Timeout.InfiniteTimeSpan));

        var provider = config.GetSection(LlmOptions.Section)["Provider"] ?? "none";
        services.AddTransient<ILlmClient>(sp => provider switch
        {
            "gemini" => sp.GetRequiredService<GeminiLlmClient>(),
            "openai_compatible" => sp.GetRequiredService<OpenAiCompatibleLlmClient>(),
            _ => new NullLlmClient(),
        });
    }

    private static void AddCoros(IServiceCollection services)
    {
        services.AddSingleton<TokenCipher>();
        services.AddSingleton<PendingBrowserLogins>();
        services.AddHttpClient<CorosOAuthClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        // No automatic retries on the MCP client: tool calls may be writes. Read retries live in CorosMcpSession.
        services.AddHttpClient("coros-mcp", c => c.Timeout = TimeSpan.FromSeconds(120));
        services.AddScoped<CorosMcpSession>();
        services.AddScoped<CorosConnectService>();
        services.AddScoped<CorosDiscovery>();
        services.AddScoped<CorosMcpGateway>();
        services.AddScoped<ICorosGateway>(sp => sp.GetRequiredService<CorosMcpGateway>());
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(s => s.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
