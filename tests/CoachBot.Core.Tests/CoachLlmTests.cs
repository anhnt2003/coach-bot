using CoachBot.Core.Abstractions;
using CoachBot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CoachBot.Core.Tests;

public class CoachLlmTests
{
    private sealed class StubClient(Func<string> answer) : ILlmClient
    {
        public string ProviderName => "stub";
        public Task<string> GenerateJsonAsync(LlmRequest request, CancellationToken ct) => Task.FromResult(answer());
    }

    private sealed class Prompts : IPromptLibrary
    {
        public string Get(string name) => name == "system" ? "lang={{language}}" : "data={{context}}";
    }

    private sealed class CapturingLog : ILlmCallLog
    {
        public List<LlmCallRecord> Records { get; } = [];

        public Task RecordAsync(LlmCallRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private static (CoachLlm Llm, CapturingLog Log) Create(Func<string> answer)
    {
        var log = new CapturingLog();
        return (new CoachLlm(new StubClient(answer), new Prompts(), log, new FakeTimeProvider(), NullLogger<CoachLlm>.Instance), log);
    }

    private static readonly Guid AthleteId = Guid.NewGuid();

    [Fact]
    public async Task A_usable_answer_is_returned_and_recorded()
    {
        var (llm, log) = Create(() => """{"summary":"good week","focus_next_week":"sleep"}""");

        var answer = await llm.AskAsync<WeeklyReportResponse>("weekly_report", AthleteId, new { km = 30 }, "en", LlmSchemas.WeeklyReport, default);

        Assert.Equal("good week", answer.Summary);
        var record = Assert.Single(log.Records);
        Assert.Null(record.Failure);
        Assert.Equal(AthleteId, record.AthleteId);
        Assert.Equal("weekly_report", record.Purpose);
        Assert.Equal("""{"km":30}""", record.ContextJson);
    }

    [Fact]
    public async Task A_gate_error_fails_the_call_and_keeps_the_raw_answer()
    {
        var (llm, log) = Create(() => """{"summary":"x","focus_next_week":"y"}""");

        var ex = await Assert.ThrowsAsync<LlmException>(() => llm.AskAsync<WeeklyReportResponse>(
            "weekly_report", AthleteId, new { }, "en", LlmSchemas.WeeklyReport, default, gate: _ => ["summary too short"]));

        Assert.Equal(LlmFailure.Integrity, ex.Reason);
        var record = Assert.Single(log.Records);
        Assert.Equal(LlmFailure.Integrity, record.Failure);
        Assert.Equal("summary too short", record.Error);
        Assert.Equal("""{"summary":"x","focus_next_week":"y"}""", record.Response);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task Unreadable_answers_are_schema_failures(string answer)
    {
        var (llm, log) = Create(() => answer);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            llm.AskAsync<WeeklyReportResponse>("weekly_report", AthleteId, new { }, "en", LlmSchemas.WeeklyReport, default));

        Assert.Equal(LlmFailure.Schema, ex.Reason);
        Assert.Equal(LlmFailure.Schema, Assert.Single(log.Records).Failure);
    }

    [Fact]
    public void Every_schema_is_valid_json()
    {
        var schemas = typeof(LlmSchemas).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral).ToList();
        Assert.True(schemas.Count >= 6);
        foreach (var field in schemas)
        {
            using var doc = System.Text.Json.JsonDocument.Parse((string)field.GetRawConstantValue()!);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Client_failures_are_recorded_and_rethrown()
    {
        var (llm, log) = Create(() => throw new LlmException("weekly_report", LlmFailure.Http, "HTTP 502"));

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            llm.AskAsync<WeeklyReportResponse>("weekly_report", AthleteId, new { }, "en", LlmSchemas.WeeklyReport, default));

        Assert.Equal(LlmFailure.Http, ex.Reason);
        var record = Assert.Single(log.Records);
        Assert.Equal(LlmFailure.Http, record.Failure);
        Assert.Null(record.Response);
    }
}
