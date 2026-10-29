using CoachBot.Core.Abstractions;
using CoachBot.Infrastructure;
using CoachBot.Infrastructure.Coros;
using CoachBot.Infrastructure.Localization;
using CoachBot.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoachBot.IntegrationTests;

/// <summary>
/// Database for integration tests, with the exact SQL files from supabase/migrations applied in order so the EF mapping is
/// verified against the real schema. Resolution order:
/// 1. COACHBOT_TEST_DATABASE_URL (any Postgres 17 connection string);
/// 2. the local Supabase database from `supabase start` (127.0.0.1:54322) if reachable — real Supabase roles/extensions;
/// 3. a Testcontainers postgres:17-alpine container (CI).
/// Each test gets its own throwaway database, dropped at the end.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string LocalSupabase = "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres;Timeout=3";
    private PostgreSqlContainer? _container;
    private readonly List<string> _databases = [];

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("COACHBOT_TEST_DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = configured;
        }
        else if (await CanConnectAsync(LocalSupabase))
        {
            ConnectionString = LocalSupabase;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var db in _databases)
        {
            await using var command = new NpgsqlCommand($"drop database if exists {db} with (force)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> CanConnectAsync(string connectionString)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return false;
        }
    }

    public static async Task ApplyMigrationsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "supabase", "migrations"), "*.sql").Order(StringComparer.Ordinal))
        {
            await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(file), connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Fresh database per test (cloned from the migrated one) so tests stay independent.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N")[..12];
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"create database {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        _databases.Add(name);
        var cs = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ToString();
        await ApplyMigrationsAsync(cs);
        return cs;
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CoachBot.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class CapturingNotifier : INotifier
{
    public List<OutboundMessage> Messages { get; } = [];

    public Task<long?> SendAsync(Guid athleteId, OutboundMessage message, CancellationToken ct)
    {
        Messages.Add(message);
        return Task.FromResult<long?>(Messages.Count);
    }
}

public static class TestHost
{
    /// <summary>
    /// The real DI graph with test doubles only at the edges: fake clock, fake COROS data, capturing notifier, strict
    /// text catalog.
    /// </summary>
    public static ServiceProvider Build(string connectionString, FakeTimeProvider time, CapturingNotifier notifier, string scenario = "normal", ILlmClient? llm = null)
    {
        const string timeZone = "Asia/Ho_Chi_Minh";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Coach:TimeZone"] = timeZone,
            ["Coach:Language"] = "en",
            ["Secrets:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Llm:Provider"] = "none",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddCoachBotCore(config);
        services.Replace(ServiceDescriptor.Singleton<INotifier>(notifier));
        services.Replace(ServiceDescriptor.Singleton<ITextCatalog>(new JsonTextCatalog(strict: true)));
        services.Replace(ServiceDescriptor.Scoped<ICorosGateway>(_ => new FakeCorosGateway(time, scenario, timeZone)));
        if (llm is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(llm));
        }
        FakeCorosGateway.PushedWorkouts.Clear();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public static CoachDbContext Db(this IServiceScope scope) => scope.ServiceProvider.GetRequiredService<CoachDbContext>();
}
